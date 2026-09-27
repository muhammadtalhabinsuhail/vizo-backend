using System.ComponentModel.DataAnnotations;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding. The column added by
/// backend/database/41_bank_statement_import.sql.
///
/// AFTER A RE-SCAFFOLD: delete this file.
/// </summary>
public partial class BankStatementLine
{
    /// <summary>
    /// The bank's own reference for the line -- a cheque number, a transfer id.
    /// Optional; what tells two same-day, same-amount lines apart.
    /// </summary>
    [MaxLength(60)]
    public string? Reference { get; set; }
}
