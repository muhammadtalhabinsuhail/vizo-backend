namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding; delete after a
/// re-scaffold, when the column will be generated inside Voucher.cs.
/// </summary>
public partial class Voucher
{
    /// <summary>
    /// A member of staff the voucher is with, in place of a customer or a
    /// supplier (database/49, the owner, 8 Nov: staff accounts on the receipt
    /// side). Posted on 2140 Staff Payables with their StaffId, so it lands on
    /// their staff ledger. Null on every voucher with a party, or with nobody.
    /// </summary>
    public int? StaffId { get; set; }
}
