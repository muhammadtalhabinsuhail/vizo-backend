using System.ComponentModel.DataAnnotations.Schema;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// BankReconciliation.cs is scaffolded and is overwritten by the next
/// dotnet ef dbcontext scaffold, so the column added by
/// backend/database/41_bank_statement_import.sql lives here, mapped by
/// convention (the property name is the quoted column name).
///
/// AFTER A RE-SCAFFOLD: delete this file -- the column will then be generated
/// inside BankReconciliation.cs itself.
/// </summary>
public partial class BankReconciliation
{
    /// <summary>
    /// The first day the bank statement covers; StatementDate is the last.
    /// Null on the reconciliations seeded before migration 41, which keep the
    /// old "a month either side of the statement date" matching window.
    /// </summary>
    [Column(TypeName = "date")]
    public DateOnly? PeriodFrom { get; set; }
}
