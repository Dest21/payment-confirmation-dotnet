using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Fudis.Payments.Data;

public static class SqlErrors
{
    /// <summary>2601 = clave duplicada en índice único; 2627 = violación de UNIQUE o PRIMARY KEY.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex?.InnerException is SqlException { Number: 2601 or 2627 };
}
