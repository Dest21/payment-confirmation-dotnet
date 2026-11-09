# Fudis · Confirmación de pagos

Revisión del endpoint `confirm-payment` y propuesta lista para producción.

- **Código original:** `codigo-original/ConfirmPayment.original.cs`, solo como referencia.
- **Propuesta:** `src/Fudis.Payments`.
- **Pruebas:** `tests/Fudis.Payments.Tests`.
- **Cambios de base de datos:** `db/001_confirmacion_pagos.sql`.

## Cómo ejecutarlo

Solo hace falta Docker, no hace falta instalar .NET:

```bash
./scripts/test.sh
```

El script levanta SQL Server 2022 en un contenedor y corre las pruebas con el SDK de .NET 10. Las pruebas usan un SQL Server real, con una base nueva por prueba, porque las garantías dependen de sus bloqueos e índices únicos. Una base en memoria no las comprobaría.

**Versiones:**
- .NET SDK 10.0.403 y ASP.NET Core 10.
- EF Core 10.0.14 (SqlServer).
- SQL Server 2022.
- xUnit 2.9.3.

## Hallazgos en el código original, por impacto

| # | Problema | Cómo se manifiesta |
|---|---|---|
| 1 | **Doble ganancia del repartidor por concurrencia.** Lee el estado, decide y escribe sin bloqueo ni restricción única (check-then-act). | La pasarela reintenta o envía dos avisos casi al mismo tiempo. Las dos peticiones leen el pedido como no pagado, y las dos lo marcan como pagado y registran una ganancia. Fudis paga dos veces al repartidor. |
| 2 | **No verifica quién envía la notificación.** | Cualquiera que conozca la URL y un `OrderId` puede marcar un pedido como pagado y recibirlo sin pagar. |
| 3 | **No valida importe, moneda ni resultado del pago.** | Un pago parcial, en otra moneda o rechazado deja el pedido como PAID. |
| 4 | **El pago y el aviso al restaurante no son atómicos.** | Si `NotifyRestaurant` falla después de guardar, responde 500. La pasarela reintenta, encuentra el pedido ya PAID y responde 200, así que **el restaurante nunca se entera** y el cliente pagó un pedido que nadie prepara. Lo mismo pasa si el proceso cae entre guardar y avisar. |
| 5 | **Marca PAID desde cualquier estado.** | Un pedido cancelado o reembolsado vuelve a quedar pagado y genera una ganancia para el repartidor. |
| 6 | **Doble cargo silencioso.** | Si llega un segundo cobro distinto para un pedido ya pagado, responde 200 y nadie revisa el posible reembolso al cliente. |
| 7 | **`DriverId` nulo.** | Si el pago llega antes de asignar repartidor, se crea una ganancia sin dueño, o el INSERT falla (columna NOT NULL), devuelve 500 y la pasarela reintenta sin fin. |
| 8 | **Sin redondeo.** | `DeliveryFee * 0.70` da montos de 3 o 4 decimales, que la columna trunca de forma implícita. |
| 9 | **Detalles.** | Estados como textos sueltos sin restricción en la base, ningún registro de las notificaciones recibidas (no se pueden auditar ni conciliar), el 70% fijo en el código y sin `CancellationToken`. |

## Propuesta

Todo ocurre en **una transacción** (`ConfirmPaymentHandler`):

1. **Registrar la notificación** en `PaymentNotifications`, con un **índice único por `EventId`**. Si llega repetida, el INSERT falla por clave duplicada y se responde 200 sin hacer nada más. Si llega en paralelo, SQL Server bloquea la segunda hasta que la primera termina.
2. **Pasar el pedido a PAID con un UPDATE condicional:** `WHERE Id = @id AND Status = 'PENDING_PAYMENT' AND Total = @amount AND Currency = @currency`. Si dos peticiones compiten, la segunda espera el bloqueo de la fila y, al volver a evaluar la condición, ya no encuentra el pedido pendiente. Gana una sola. También se guarda `PaidPaymentId`, el cobro que pagó el pedido.
3. **Registrar la ganancia del repartidor** con el 70% configurable, redondeada a céntimos con mitad hacia arriba y guardando el porcentaje aplicado. Un **índice único por `OrderId`** es la última defensa, aunque la lógica falle. Si no hay repartidor, la ganancia queda `PENDING_ASSIGNMENT`.
4. **Guardar el aviso al restaurante en un outbox** (`OutboxMessages`), en la misma transacción. Un proceso aparte (`OutboxProcessor`) lo envía con reintentos y espera exponencial. Si se agotan los reintentos, el mensaje queda como fallido para revisarlo.

**Antes de la transacción**, el controlador:
- verifica la **firma HMAC-SHA256** del cuerpo crudo con un timestamp de máximo 5 minutos, comparando en tiempo constante;
- valida el contrato;
- ignora los pagos que no están `APPROVED`.

**Respuestas:**

| Caso | Respuesta | Qué hace |
|---|---|---|
| Pago aplicado, notificación repetida o pedido ya pagado con el mismo cobro | 200 | La pasarela deja de reintentar. |
| Importe distinto | 422 | Se registra y se genera una alerta para revisión. |
| Pedido cancelado | 409 | Se registra y se genera una alerta para revisar el reembolso. |
| Pedido ya pagado con **otro** cobro | 200 | Se genera una alerta por posible doble cargo. |
| Pedido inexistente | 404 | **No** se guarda nada, para que la pasarela reintente (el pedido podría crearse después). |
| Firma inválida | 401 | Nada. |

## ¿Qué pasa si la pasarela envía la misma notificación varias veces?

- **Mismo `EventId`:** solo la primera tiene efecto. Las demás chocan con el índice único (o esperan a la primera, si llegan a la vez) y responden 200 con `Duplicate`. Hay una sola ganancia y un solo aviso. Probado con 20 envíos en paralelo.
- **`EventId` distinto pero mismo cobro** (la pasarela regenera el id al reintentar): el UPDATE condicional deja pasar solo una. Las demás responden `AlreadyPaid`, sin alerta. Probado con 10 envíos en paralelo.
- **Un cobro distinto** para un pedido ya pagado: no se paga nada más, pero se genera una alerta de posible doble cargo para revisar el reembolso.

## ¿Cómo se mantiene la consistencia entre pago, ganancia y aviso?

- **El pago y la ganancia** se guardan en la misma transacción: o están los dos o ninguno. Además, la base lo garantiza por sí misma con el índice único por pedido.
- **El aviso al restaurante** es un efecto externo y no puede entrar en la transacción de la base. Por eso se usa el **patrón outbox**: el mensaje se guarda **junto con** el pago y se envía después. Si el envío falla, el pago no se pierde ni se revierte, y el aviso se reintenta.
- **La entrega es al menos una vez.** El aviso puede llegar dos veces si el proceso cae justo después de enviarlo, así que lleva un `MessageId` para que el receptor descarte duplicados.
- **Varias instancias del procesador** pueden correr a la vez: `UPDLOCK, READPAST` reparte las filas sin que dos tomen el mismo mensaje.

## Contrato de entrada propuesto

`POST /payments/confirm-payment`

**Cabeceras:**
- `X-Fudis-Timestamp`: segundos Unix.
- `X-Fudis-Signature`: hex(HMAC-SHA256(secreto, `"{timestamp}.{cuerpo}"`)).

**Cuerpo:**
```json
{
  "eventId": "evt_123",
  "paymentId": "pay_456",
  "orderId": 1001,
  "amount": 50.00,
  "currency": "PEN",
  "status": "APPROVED",
  "occurredAt": "2026-11-09T15:00:00Z"
}
```

| Campo | Para qué sirve |
|---|---|
| `eventId` | Clave de idempotencia. |
| `paymentId` | Identifica el cobro; sirve para detectar dobles cargos. |
| `amount` / `currency` | Deben coincidir con `Total` / `Currency` del pedido. |
| `status` | Solo `APPROVED` confirma el pedido. |

## Supuestos

- El pedido tiene (o se le agrega) `Total` y `Currency`. Sin eso no se puede validar el importe.
- Los estados del pedido son `PENDING_PAYMENT`, `PAID` y `CANCELLED`. En producción habría que alinearlos con los reales.
- La pasarela firma con un secreto compartido y reintenta mientras no reciba una respuesta 2xx. Si la pasarela real usa otro esquema de firma, solo cambia `SignatureVerifier`.
- El 70% es configurable (`Payments:DriverShare`) y se redondea a céntimos con mitad hacia arriba: 5.005 → 5.01. Negocio debe confirmar esa regla.
- Si el pedido no tiene repartidor, la ganancia queda pendiente y se completa al asignarlo. Ese flujo no está incluido aquí.

## Pruebas

**Ejecutadas: 22, todas pasan** (`./scripts/test.sh`; resultados en `test-results/resultados.trx`). Las dos pruebas de concurrencia se repitieron 5 veces seguidas sin fallos.

| Prueba | Qué comprueba |
|---|---|
| Pago normal | El pedido queda PAID, con una ganancia de 7.00 sobre una tarifa de 10 y un aviso en el outbox. |
| Misma notificación 2 veces | Sin duplicados. |
| Misma notificación **20 veces en paralelo** | Una sola ganancia. |
| **10 notificaciones distintas del mismo cobro en paralelo** | Una sola ganancia y sin alertas. |
| Segundo cobro distinto | Alerta de doble cargo, sin una segunda ganancia. |
| Importe distinto | No se paga y se genera una alerta. |
| Pedido cancelado | No se paga, sin ganancia y con alerta. |
| Pedido inexistente | No se guarda nada, para que la pasarela reintente. |
| Pedido sin repartidor | La ganancia queda `PENDING_ASSIGNMENT`. |
| Redondeo | 7.15 → 5.01, 4.99 → 3.49, 0 → 0. |
| Índice único en la base | Rechaza una segunda ganancia aunque la aplicación falle. |
| Outbox | Si el aviso falla, el pago se mantiene y el aviso se reintenta después de la espera. Tras 8 fallos queda como fallido. |
| Firma | Válida, cuerpo alterado, otro secreto, sin firma, firma vencida y firma no hexadecimal. |
| Punta a punta por HTTP | Firma inválida → 401. La misma notificación dos veces → 200 `Applied` y luego 200 `Duplicate`, con una sola ganancia. |

**Propuestas, no implementadas:**
- Prueba de carga del outbox con varias instancias en paralelo.
- Prueba de caída del proceso entre el envío del aviso y el `SaveChanges` del outbox (la entrega duplicada esperada).
- Pruebas contra el esquema y los datos reales de Fudis.

## Limitaciones y trabajo pendiente

- **El flujo de asignación de repartidor** debe completar las ganancias `PENDING_ASSIGNMENT`. No está incluido.
- **Los avisos de revisión** (`PaymentRequiresReview`) hoy solo se registran en el log. Falta conectarlos a un canal real, como Slack, correo o un panel de operaciones.
- **El procesador del outbox mantiene la transacción abierta mientras envía el lote.** Con lotes pequeños es aceptable. Con volumen alto convendría tomar los mensajes con una "reserva" con vencimiento y enviar fuera de la transacción.
- **Los mensajes fallidos** necesitan un panel o un job para revisarlos y reintentarlos a mano.
- **Rotación del secreto de la firma:** aceptar dos secretos durante el cambio.
- **Sin migraciones de EF.** Se entrega el script SQL manual, porque el esquema real es desconocido. Las pruebas crean el esquema desde el modelo.

## Uso de IA

Usé Claude (Anthropic) como asistente para:
- revisar el código original y discutir los riesgos;
- generar el borrador de la implementación, las pruebas y este README.

Lo que verifiqué yo:
- la lógica de concurrencia (UPDATE condicional, índices únicos y la elección de comparar contra `PaidPaymentId` en lugar de leer otras notificaciones, para evitar bloqueos mutuos entre transacciones);
- los resultados de las 22 pruebas contra SQL Server real.

Corregí:
- un posible deadlock en la detección de doble cargo;
- dependencias con avisos de seguridad (`System.Security.Cryptography.Pkcs`).
