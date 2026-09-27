using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// Delivery.cs is scaffolded and is overwritten by the next
/// dotnet ef dbcontext scaffold, so the columns this project added live here.
/// Created by backend/database/39_visits_and_delivery_confirmation.sql and
/// mapped with annotations, so nothing is added to AppDbContext.cs (Talha's).
/// Deliberately no navigation properties: the ids are read and written, never
/// joined through, so no relationship has to be configured.
///
/// AFTER A RE-SCAFFOLD: delete this file, or you will get a duplicate
/// definition once the columns exist on the database being scaffolded.
/// </summary>
public partial class Delivery
{
    /// <summary>Who signed for the goods at the shop -- the first question when a customer says it never came.</summary>
    [MaxLength(100)]
    public string? ReceivedBy { get; set; }

    /// <summary>When Mark delivered was pressed. DeliveredDate is the day it arrived, which may be earlier.</summary>
    [Column(TypeName = "timestamp without time zone")]
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>
    /// The confirmed collection that settled this delivery's COD -- and through
    /// it the receipt voucher that put the money in the bank and took it off the
    /// customer's account. Null on anything settled before 39 existed.
    /// </summary>
    public int? CodCollectionId { get; set; }

    /// <summary>The day the courier's money landed.</summary>
    public DateOnly? CodSettledOn { get; set; }

    /// <summary>What the courier kept out of the COD before paying the rest over.</summary>
    [Precision(14, 2)]
    public decimal CodFeeAmount { get; set; }

    /// <summary>The journal entry that posted that fee (Dr 5114 Delivery &amp; Courier, Cr the bank).</summary>
    public int? CodFeeEntryId { get; set; }
}
