using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class User
{
    public int UserId { get; set; }

    public int RoleId { get; set; }

    public bool RequiresEmail { get; set; }

    public string FullName { get; set; } = null!;

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? PasswordHash { get; set; }

    public int? PrimaryLocationId { get; set; }

    public bool IsActive { get; set; }

    public DateOnly CreatedAt { get; set; }

    public virtual ICollection<ActivityLog> ActivityLogs { get; set; } = new List<ActivityLog>();

    public virtual ICollection<BackupHistory> BackupHistories { get; set; } = new List<BackupHistory>();

    public virtual Employee? Employee { get; set; }

    public virtual ICollection<Expense> Expenses { get; set; } = new List<Expense>();

    public virtual ICollection<FiscalPeriod> FiscalPeriods { get; set; } = new List<FiscalPeriod>();

    public virtual ICollection<JournalEntry> JournalEntryCreatedByUsers { get; set; } = new List<JournalEntry>();

    public virtual ICollection<JournalEntry> JournalEntryPostedByUsers { get; set; } = new List<JournalEntry>();

    public virtual ICollection<Location> Locations { get; set; } = new List<Location>();

    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    public virtual Party? Party { get; set; }

    public virtual Location? PrimaryLocation { get; set; }

    public virtual Role Role { get; set; } = null!;

    public virtual Role RoleNavigation { get; set; } = null!;

    public virtual ICollection<SalesInvoice> SalesInvoices { get; set; } = new List<SalesInvoice>();

    public virtual ICollection<SalesOrder> SalesOrders { get; set; } = new List<SalesOrder>();

    public virtual ICollection<SalesReturn> SalesReturns { get; set; } = new List<SalesReturn>();

    public virtual ICollection<StockMovement> StockMovements { get; set; } = new List<StockMovement>();

    public virtual ICollection<UserPreference> UserPreferences { get; set; } = new List<UserPreference>();

    public virtual ICollection<Voucher> Vouchers { get; set; } = new List<Voucher>();

    public virtual ICollection<Location> LocationsNavigation { get; set; } = new List<Location>();
}
