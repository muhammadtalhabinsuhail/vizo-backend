using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

public partial class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Account> Accounts { get; set; }

    public virtual DbSet<AccountGroup> AccountGroups { get; set; }

    public virtual DbSet<AccountType> AccountTypes { get; set; }

    public virtual DbSet<ActivityLog> ActivityLogs { get; set; }

    public virtual DbSet<AdjustmentReason> AdjustmentReasons { get; set; }

    public virtual DbSet<AppSetting> AppSettings { get; set; }

    public virtual DbSet<BackupHistory> BackupHistories { get; set; }

    public virtual DbSet<BackupStatus> BackupStatuses { get; set; }

    public virtual DbSet<BackupType> BackupTypes { get; set; }

    public virtual DbSet<BankReconciliation> BankReconciliations { get; set; }

    public virtual DbSet<BankStatementLine> BankStatementLines { get; set; }

    public virtual DbSet<Brand> Brands { get; set; }

    public virtual DbSet<Category> Categories { get; set; }

    public virtual DbSet<City> Cities { get; set; }

    public virtual DbSet<Claim> Claims { get; set; }

    public virtual DbSet<ClaimOutcome> ClaimOutcomes { get; set; }

    public virtual DbSet<ClaimReason> ClaimReasons { get; set; }

    public virtual DbSet<ClaimStage> ClaimStages { get; set; }

    public virtual DbSet<Collection> Collections { get; set; }

    public virtual DbSet<CollectionAllocation> CollectionAllocations { get; set; }

    public virtual DbSet<CollectionStatus> CollectionStatuses { get; set; }

    public virtual DbSet<Company> Companies { get; set; }

    public virtual DbSet<Courier> Couriers { get; set; }

    public virtual DbSet<CreditHoldPolicy> CreditHoldPolicies { get; set; }

    public virtual DbSet<CustomerVisit> CustomerVisits { get; set; }

    public virtual DbSet<Delivery> Deliveries { get; set; }

    public virtual DbSet<DeliveryChannel> DeliveryChannels { get; set; }

    public virtual DbSet<DeliveryStatus> DeliveryStatuses { get; set; }

    public virtual DbSet<DocumentSeries> DocumentSeries { get; set; }

    public virtual DbSet<Employee> Employees { get; set; }

    public virtual DbSet<Expense> Expenses { get; set; }

    public virtual DbSet<FiscalPeriod> FiscalPeriods { get; set; }

    public virtual DbSet<GoodsReceipt> GoodsReceipts { get; set; }

    public virtual DbSet<GoodsReceiptItem> GoodsReceiptItems { get; set; }

    public virtual DbSet<InvoiceStatus> InvoiceStatuses { get; set; }

    public virtual DbSet<JournalEntry> JournalEntries { get; set; }

    public virtual DbSet<JournalEntryLine> JournalEntryLines { get; set; }

    public virtual DbSet<JournalEntryType> JournalEntryTypes { get; set; }

    public virtual DbSet<Location> Locations { get; set; }

    public virtual DbSet<LocationKind> LocationKinds { get; set; }

    public virtual DbSet<MovementType> MovementTypes { get; set; }

    public virtual DbSet<Notification> Notifications { get; set; }

    public virtual DbSet<OrderStatus> OrderStatuses { get; set; }

    public virtual DbSet<Party> Parties { get; set; }

    public virtual DbSet<PartyCategory> PartyCategories { get; set; }

    public virtual DbSet<PaymentMethod> PaymentMethods { get; set; }

    public virtual DbSet<Permission> Permissions { get; set; }

    public virtual DbSet<PostingStatus> PostingStatuses { get; set; }

    public virtual DbSet<Product> Products { get; set; }

    public virtual DbSet<ProductBarcode> ProductBarcodes { get; set; }

    public virtual DbSet<Province> Provinces { get; set; }

    public virtual DbSet<PurchaseInvoice> PurchaseInvoices { get; set; }

    public virtual DbSet<PurchaseInvoiceItem> PurchaseInvoiceItems { get; set; }

    public virtual DbSet<PurchaseOrder> PurchaseOrders { get; set; }

    public virtual DbSet<PurchaseOrderItem> PurchaseOrderItems { get; set; }

    public virtual DbSet<PurchaseOrderStatus> PurchaseOrderStatuses { get; set; }

    public virtual DbSet<PurchaseReturn> PurchaseReturns { get; set; }

    public virtual DbSet<PurchaseReturnItem> PurchaseReturnItems { get; set; }

    public virtual DbSet<ReturnCondition> ReturnConditions { get; set; }

    public virtual DbSet<ReturnStatus> ReturnStatuses { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<SalesInvoice> SalesInvoices { get; set; }

    public virtual DbSet<SalesInvoiceItem> SalesInvoiceItems { get; set; }

    public virtual DbSet<SalesOrder> SalesOrders { get; set; }

    public virtual DbSet<SalesOrderItem> SalesOrderItems { get; set; }

    public virtual DbSet<SalesReturn> SalesReturns { get; set; }

    public virtual DbSet<SalesReturnItem> SalesReturnItems { get; set; }

    public virtual DbSet<SeverityLevel> SeverityLevels { get; set; }

    public virtual DbSet<StockAdjustment> StockAdjustments { get; set; }

    public virtual DbSet<StockAdjustmentItem> StockAdjustmentItems { get; set; }

    public virtual DbSet<StockBalance> StockBalances { get; set; }

    public virtual DbSet<StockMovement> StockMovements { get; set; }

    public virtual DbSet<StockTransfer> StockTransfers { get; set; }

    public virtual DbSet<StockTransferItem> StockTransferItems { get; set; }

    public virtual DbSet<TransferStatus> TransferStatuses { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserPreference> UserPreferences { get; set; }

    public virtual DbSet<VisitOutcome> VisitOutcomes { get; set; }

    public virtual DbSet<Voucher> Vouchers { get; set; }

    public virtual DbSet<VoucherAllocation> VoucherAllocations { get; set; }

    public virtual DbSet<VoucherType> VoucherTypes { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.AccountId).HasName("Account_pkey");

            entity.ToTable("Account");

            entity.HasIndex(e => e.AccountCode, "Account_AccountCode_key").IsUnique();

            entity.Property(e => e.AccountCode).HasMaxLength(15);
            entity.Property(e => e.AccountName).HasMaxLength(100);
            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .HasDefaultValueSql("'PKR'::bpchar")
                .IsFixedLength();
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.IsGroup).HasDefaultValue(false);
            entity.Property(e => e.OpeningBalance).HasPrecision(14, 2);

            entity.HasOne(d => d.AccountType).WithMany(p => p.Accounts)
                .HasForeignKey(d => d.AccountTypeId)
                .HasConstraintName("fk_account_type");

            entity.HasOne(d => d.ParentAccount).WithMany(p => p.InverseParentAccount)
                .HasForeignKey(d => d.ParentAccountId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_account_parent");
        });

        modelBuilder.Entity<AccountGroup>(entity =>
        {
            entity.HasKey(e => e.GroupId).HasName("AccountGroup_pkey");

            entity.ToTable("AccountGroup");

            entity.HasIndex(e => e.GroupName, "AccountGroup_GroupName_key").IsUnique();

            entity.Property(e => e.GroupName).HasMaxLength(40);
        });

        modelBuilder.Entity<AccountType>(entity =>
        {
            entity.HasKey(e => e.AccountTypeId).HasName("AccountType_pkey");

            entity.ToTable("AccountType");

            entity.HasIndex(e => e.CodePrefix, "AccountType_CodePrefix_key").IsUnique();

            entity.HasIndex(e => e.TypeName, "AccountType_TypeName_key").IsUnique();

            entity.Property(e => e.CodeLength).HasDefaultValue((short)7);
            entity.Property(e => e.CodePrefix).HasMaxLength(6);
            entity.Property(e => e.IsSystem).HasDefaultValue(true);
            entity.Property(e => e.TypeName).HasMaxLength(60);

            entity.HasOne(d => d.Group).WithMany(p => p.AccountTypes)
                .HasForeignKey(d => d.GroupId)
                .HasConstraintName("fk_acctype_group");
        });

        modelBuilder.Entity<ActivityLog>(entity =>
        {
            entity.HasKey(e => e.LogId).HasName("ActivityLog_pkey");

            entity.ToTable("ActivityLog");

            entity.Property(e => e.ActionName).HasMaxLength(30);
            entity.Property(e => e.Detail).HasMaxLength(300);
            entity.Property(e => e.EntityReference).HasMaxLength(60);
            entity.Property(e => e.EntityType).HasMaxLength(40);
            entity.Property(e => e.IpAddress).HasMaxLength(45);
            entity.Property(e => e.LoggedAt).HasColumnType("timestamp without time zone");

            entity.HasOne(d => d.Location).WithMany(p => p.ActivityLogs)
                .HasForeignKey(d => d.LocationId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_log_location");

            entity.HasOne(d => d.Severity).WithMany(p => p.ActivityLogs)
                .HasForeignKey(d => d.SeverityId)
                .HasConstraintName("fk_log_severity");

            entity.HasOne(d => d.User).WithMany(p => p.ActivityLogs)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_log_user");
        });

        modelBuilder.Entity<AdjustmentReason>(entity =>
        {
            entity.HasKey(e => e.ReasonId).HasName("AdjustmentReason_pkey");

            entity.ToTable("AdjustmentReason");

            entity.HasIndex(e => e.ReasonKey, "AdjustmentReason_ReasonKey_key").IsUnique();

            entity.Property(e => e.ReasonKey).HasMaxLength(25);
            entity.Property(e => e.ReasonName).HasMaxLength(60);
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(e => e.SettingId).HasName("AppSetting_pkey");

            entity.ToTable("AppSetting");

            entity.HasIndex(e => e.SettingKey, "AppSetting_SettingKey_key").IsUnique();

            entity.Property(e => e.Description).HasMaxLength(200);
            entity.Property(e => e.SettingGroup).HasMaxLength(30);
            entity.Property(e => e.SettingKey).HasMaxLength(60);
            entity.Property(e => e.SettingValue).HasMaxLength(200);
        });

        modelBuilder.Entity<BackupHistory>(entity =>
        {
            entity.HasKey(e => e.BackupId).HasName("BackupHistory_pkey");

            entity.ToTable("BackupHistory");

            entity.Property(e => e.ChecksumHash).HasMaxLength(80);
            entity.Property(e => e.Destination).HasMaxLength(80);
            entity.Property(e => e.DurationSeconds).HasDefaultValue(0);
            entity.Property(e => e.SizeMb).HasPrecision(12, 2);
            entity.Property(e => e.StartedAt).HasColumnType("timestamp without time zone");

            entity.HasOne(d => d.BackupType).WithMany(p => p.BackupHistories)
                .HasForeignKey(d => d.BackupTypeId)
                .HasConstraintName("fk_backup_type");

            entity.HasOne(d => d.Status).WithMany(p => p.BackupHistories)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_backup_status");

            entity.HasOne(d => d.TriggeredByUser).WithMany(p => p.BackupHistories)
                .HasForeignKey(d => d.TriggeredByUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_backup_user");
        });

        modelBuilder.Entity<BackupStatus>(entity =>
        {
            entity.HasKey(e => e.StatusId).HasName("BackupStatus_pkey");

            entity.ToTable("BackupStatus");

            entity.HasIndex(e => e.StatusKey, "BackupStatus_StatusKey_key").IsUnique();

            entity.Property(e => e.StatusKey).HasMaxLength(20);
            entity.Property(e => e.StatusName).HasMaxLength(40);
        });

        modelBuilder.Entity<BackupType>(entity =>
        {
            entity.HasKey(e => e.BackupTypeId).HasName("BackupType_pkey");

            entity.ToTable("BackupType");

            entity.HasIndex(e => e.TypeKey, "BackupType_TypeKey_key").IsUnique();

            entity.Property(e => e.TypeKey).HasMaxLength(20);
            entity.Property(e => e.TypeName).HasMaxLength(40);
        });

        modelBuilder.Entity<BankReconciliation>(entity =>
        {
            entity.HasKey(e => e.ReconciliationId).HasName("BankReconciliation_pkey");

            entity.ToTable("BankReconciliation");

            entity.HasIndex(e => new { e.AccountId, e.StatementDate }, "uq_recon_account_date").IsUnique();

            entity.Property(e => e.ClosingBalance).HasPrecision(14, 2);
            entity.Property(e => e.OpeningBalance).HasPrecision(14, 2);

            entity.HasOne(d => d.Account).WithMany(p => p.BankReconciliations)
                .HasForeignKey(d => d.AccountId)
                .HasConstraintName("fk_recon_account");

            entity.HasOne(d => d.PreparedByUser).WithMany(p => p.BankReconciliations)
                .HasForeignKey(d => d.PreparedByUserId)
                .HasConstraintName("fk_recon_preparer");

            entity.HasOne(d => d.Status).WithMany(p => p.BankReconciliations)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_recon_status");
        });

        modelBuilder.Entity<BankStatementLine>(entity =>
        {
            entity.HasKey(e => e.StatementLineId).HasName("BankStatementLine_pkey");

            entity.ToTable("BankStatementLine");

            entity.Property(e => e.Amount).HasPrecision(14, 2);
            entity.Property(e => e.Description).HasMaxLength(200);

            entity.HasOne(d => d.MatchedLine).WithMany(p => p.BankStatementLines)
                .HasForeignKey(d => d.MatchedLineId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_bsl_matched");

            entity.HasOne(d => d.Reconciliation).WithMany(p => p.BankStatementLines)
                .HasForeignKey(d => d.ReconciliationId)
                .HasConstraintName("fk_bsl_recon");
        });

        modelBuilder.Entity<Brand>(entity =>
        {
            entity.HasKey(e => e.BrandId).HasName("Brand_pkey");

            entity.ToTable("Brand");

            entity.HasIndex(e => e.BrandCode, "Brand_BrandCode_key").IsUnique();

            entity.HasIndex(e => e.BrandName, "Brand_BrandName_key").IsUnique();

            entity.Property(e => e.BrandCode)
                .HasMaxLength(2)
                .IsFixedLength();
            entity.Property(e => e.BrandName).HasMaxLength(60);
            entity.Property(e => e.Description).HasMaxLength(150);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasKey(e => e.CategoryId).HasName("Category_pkey");

            entity.ToTable("Category");

            entity.HasIndex(e => e.CategoryName, "Category_CategoryName_key").IsUnique();

            entity.Property(e => e.CategoryName).HasMaxLength(80);
            entity.Property(e => e.IsActive).HasDefaultValue(true);

            entity.HasOne(d => d.ParentCategory).WithMany(p => p.InverseParentCategory)
                .HasForeignKey(d => d.ParentCategoryId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_category_parent");
        });

        modelBuilder.Entity<City>(entity =>
        {
            entity.HasKey(e => e.CityId).HasName("City_pkey");

            entity.ToTable("City");

            entity.HasIndex(e => new { e.CityName, e.ProvinceId }, "uq_city_in_province").IsUnique();

            entity.Property(e => e.CityName).HasMaxLength(80);

            entity.HasOne(d => d.Province).WithMany(p => p.Cities)
                .HasForeignKey(d => d.ProvinceId)
                .HasConstraintName("fk_city_province");
        });

        modelBuilder.Entity<Claim>(entity =>
        {
            entity.HasKey(e => e.ClaimId).HasName("Claim_pkey");

            entity.ToTable("Claim");

            entity.HasIndex(e => e.ClaimNo, "Claim_ClaimNo_key").IsUnique();

            entity.Property(e => e.ClaimNo).HasMaxLength(20);
            entity.Property(e => e.ClaimNote).HasMaxLength(300);
            entity.Property(e => e.OriginalOrderNo).HasMaxLength(20);
            entity.Property(e => e.RemindersSent).HasDefaultValue((short)0);
            entity.Property(e => e.SupplierNote).HasMaxLength(300);
            entity.Property(e => e.UnitCost).HasPrecision(14, 2);

            entity.HasOne(d => d.CustomerUser).WithMany(p => p.ClaimCustomerUsers)
                .HasForeignKey(d => d.CustomerUserId)
                .HasConstraintName("fk_claim_customer");

            entity.HasOne(d => d.Outcome).WithMany(p => p.Claims)
                .HasForeignKey(d => d.OutcomeId)
                .HasConstraintName("fk_claim_outcome");

            entity.HasOne(d => d.Product).WithMany(p => p.Claims)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_claim_product");

            entity.HasOne(d => d.Reason).WithMany(p => p.Claims)
                .HasForeignKey(d => d.ReasonId)
                .HasConstraintName("fk_claim_reason");

            entity.HasOne(d => d.ReceivedByUser).WithMany(p => p.Claims)
                .HasForeignKey(d => d.ReceivedByUserId)
                .HasConstraintName("fk_claim_receiver");

            entity.HasOne(d => d.Stage).WithMany(p => p.Claims)
                .HasForeignKey(d => d.StageId)
                .HasConstraintName("fk_claim_stage");

            entity.HasOne(d => d.SupplierUser).WithMany(p => p.ClaimSupplierUsers)
                .HasForeignKey(d => d.SupplierUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_claim_supplier");
        });

        modelBuilder.Entity<ClaimOutcome>(entity =>
        {
            entity.HasKey(e => e.OutcomeId).HasName("ClaimOutcome_pkey");

            entity.ToTable("ClaimOutcome");

            entity.HasIndex(e => e.OutcomeKey, "ClaimOutcome_OutcomeKey_key").IsUnique();

            entity.Property(e => e.OutcomeKey).HasMaxLength(20);
            entity.Property(e => e.OutcomeName).HasMaxLength(60);
        });

        modelBuilder.Entity<ClaimReason>(entity =>
        {
            entity.HasKey(e => e.ReasonId).HasName("ClaimReason_pkey");

            entity.ToTable("ClaimReason");

            entity.HasIndex(e => e.ReasonKey, "ClaimReason_ReasonKey_key").IsUnique();

            entity.Property(e => e.ReasonKey).HasMaxLength(20);
            entity.Property(e => e.ReasonName).HasMaxLength(60);
            entity.Property(e => e.UsuallyAccepted).HasDefaultValue(true);
        });

        modelBuilder.Entity<ClaimStage>(entity =>
        {
            entity.HasKey(e => e.StageId).HasName("ClaimStage_pkey");

            entity.ToTable("ClaimStage");

            entity.HasIndex(e => e.StageKey, "ClaimStage_StageKey_key").IsUnique();

            entity.Property(e => e.IsOpen).HasDefaultValue(false);
            entity.Property(e => e.StageKey).HasMaxLength(20);
            entity.Property(e => e.StageName).HasMaxLength(40);
        });

        modelBuilder.Entity<Collection>(entity =>
        {
            entity.HasKey(e => e.CollectionId).HasName("Collection_pkey");

            entity.ToTable("Collection");

            entity.HasIndex(e => e.ReceiptNo, "Collection_ReceiptNo_key").IsUnique();

            entity.Property(e => e.Amount).HasPrecision(14, 2);
            entity.Property(e => e.BankName).HasMaxLength(60);
            entity.Property(e => e.Note).HasMaxLength(300);
            entity.Property(e => e.ReceiptNo).HasMaxLength(20);
            entity.Property(e => e.ReferenceNo).HasMaxLength(50);

            entity.HasOne(d => d.CollectedByUser).WithMany(p => p.CollectionCollectedByUsers)
                .HasForeignKey(d => d.CollectedByUserId)
                .HasConstraintName("fk_collection_collector");

            entity.HasOne(d => d.ConfirmedByUser).WithMany(p => p.CollectionConfirmedByUsers)
                .HasForeignKey(d => d.ConfirmedByUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_collection_confirmer");

            entity.HasOne(d => d.CustomerUser).WithMany(p => p.Collections)
                .HasForeignKey(d => d.CustomerUserId)
                .HasConstraintName("fk_collection_customer");

            entity.HasOne(d => d.Method).WithMany(p => p.Collections)
                .HasForeignKey(d => d.MethodId)
                .HasConstraintName("fk_collection_method");

            entity.HasOne(d => d.Status).WithMany(p => p.Collections)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_collection_status");

            entity.HasOne(d => d.Voucher).WithMany(p => p.Collections)
                .HasForeignKey(d => d.VoucherId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_collection_voucher");
        });

        modelBuilder.Entity<CollectionAllocation>(entity =>
        {
            entity.HasKey(e => e.AllocationId).HasName("CollectionAllocation_pkey");

            entity.ToTable("CollectionAllocation");

            entity.Property(e => e.Amount).HasPrecision(14, 2);

            entity.HasOne(d => d.Collection).WithMany(p => p.CollectionAllocations)
                .HasForeignKey(d => d.CollectionId)
                .HasConstraintName("fk_colalloc_collection");

            entity.HasOne(d => d.Order).WithMany(p => p.CollectionAllocations)
                .HasForeignKey(d => d.OrderId)
                .HasConstraintName("fk_colalloc_order");
        });

        modelBuilder.Entity<CollectionStatus>(entity =>
        {
            entity.HasKey(e => e.StatusId).HasName("CollectionStatus_pkey");

            entity.ToTable("CollectionStatus");

            entity.HasIndex(e => e.StatusKey, "CollectionStatus_StatusKey_key").IsUnique();

            entity.Property(e => e.StatusKey).HasMaxLength(20);
            entity.Property(e => e.StatusName).HasMaxLength(40);
        });

        modelBuilder.Entity<Company>(entity =>
        {
            entity.HasKey(e => e.CompanyId).HasName("Company_pkey");

            entity.ToTable("Company");

            entity.HasIndex(e => e.Email, "Company_Email_key").IsUnique();

            entity.HasIndex(e => e.Ntn, "Company_Ntn_key").IsUnique();

            entity.HasIndex(e => e.Strn, "Company_Strn_key").IsUnique();

            entity.Property(e => e.AddressLine).HasMaxLength(200);
            entity.Property(e => e.CompanyName).HasMaxLength(120);
            entity.Property(e => e.Country)
                .HasMaxLength(60)
                .HasDefaultValueSql("'Pakistan'::character varying");
            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .HasDefaultValueSql("'PKR'::bpchar")
                .IsFixedLength();
            entity.Property(e => e.CurrencySymbol)
                .HasMaxLength(6)
                .HasDefaultValueSql("'PKR'::character varying");
            entity.Property(e => e.Email).HasMaxLength(120);
            entity.Property(e => e.FiscalYearStartMonth).HasDefaultValue((short)10);
            entity.Property(e => e.ForeignRate)
                .HasPrecision(12, 4)
                .HasDefaultValueSql("1.0000");
            entity.Property(e => e.LegalName).HasMaxLength(150);
            entity.Property(e => e.Ntn).HasMaxLength(20);
            entity.Property(e => e.Phone).HasMaxLength(30);
            entity.Property(e => e.Strn).HasMaxLength(30);

            entity.HasOne(d => d.City).WithMany(p => p.Companies)
                .HasForeignKey(d => d.CityId)
                .HasConstraintName("fk_company_city");
        });

        modelBuilder.Entity<Courier>(entity =>
        {
            entity.HasKey(e => e.CourierId).HasName("Courier_pkey");

            entity.ToTable("Courier");

            entity.HasIndex(e => e.CourierName, "Courier_CourierName_key").IsUnique();

            entity.Property(e => e.BookingCharge).HasPrecision(14, 2);
            entity.Property(e => e.CodFeePercent).HasPrecision(5, 2);
            entity.Property(e => e.CodSettlementDays).HasDefaultValue((short)0);
            entity.Property(e => e.ContactPerson).HasMaxLength(80);
            entity.Property(e => e.CourierName).HasMaxLength(80);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Phone).HasMaxLength(30);
            entity.Property(e => e.ShortName).HasMaxLength(30);
            entity.Property(e => e.TrackingUrlTemplate).HasMaxLength(200);
        });

        modelBuilder.Entity<CreditHoldPolicy>(entity =>
        {
            entity.HasKey(e => e.PolicyId).HasName("CreditHoldPolicy_pkey");

            entity.ToTable("CreditHoldPolicy");

            entity.HasIndex(e => e.PolicyKey, "CreditHoldPolicy_PolicyKey_key").IsUnique();

            entity.Property(e => e.Description).HasMaxLength(140);
            entity.Property(e => e.PolicyKey).HasMaxLength(10);
            entity.Property(e => e.PolicyName).HasMaxLength(40);
        });

        modelBuilder.Entity<CustomerVisit>(entity =>
        {
            entity.HasKey(e => e.VisitId).HasName("CustomerVisit_pkey");

            entity.ToTable("CustomerVisit");

            entity.Property(e => e.Latitude).HasPrecision(9, 6);
            entity.Property(e => e.Longitude).HasPrecision(9, 6);
            entity.Property(e => e.Notes).HasMaxLength(300);
            entity.Property(e => e.VisitedAt).HasColumnType("timestamp without time zone");

            entity.HasOne(d => d.CustomerUser).WithMany(p => p.CustomerVisits)
                .HasForeignKey(d => d.CustomerUserId)
                .HasConstraintName("fk_visit_customer");

            entity.HasOne(d => d.Outcome).WithMany(p => p.CustomerVisits)
                .HasForeignKey(d => d.OutcomeId)
                .HasConstraintName("fk_visit_outcome");

            entity.HasOne(d => d.SalesPersonUser).WithMany(p => p.CustomerVisits)
                .HasForeignKey(d => d.SalesPersonUserId)
                .HasConstraintName("fk_visit_rep");
        });

        modelBuilder.Entity<Delivery>(entity =>
        {
            entity.HasKey(e => e.DeliveryId).HasName("Delivery_pkey");

            entity.ToTable("Delivery");

            entity.HasIndex(e => e.DeliveryNo, "Delivery_DeliveryNo_key").IsUnique();

            entity.Property(e => e.BookingCharge).HasPrecision(14, 2);
            entity.Property(e => e.CodAmount).HasPrecision(14, 2);
            entity.Property(e => e.DeliveryNo).HasMaxLength(20);
            entity.Property(e => e.IsCodSettled).HasDefaultValue(false);
            entity.Property(e => e.Notes).HasMaxLength(300);
            entity.Property(e => e.Parcels).HasDefaultValue(1);
            entity.Property(e => e.RemindersSent).HasDefaultValue((short)0);
            entity.Property(e => e.TrackingNo).HasMaxLength(40);
            entity.Property(e => e.WeightKg).HasPrecision(10, 2);

            entity.HasOne(d => d.Channel).WithMany(p => p.Deliveries)
                .HasForeignKey(d => d.ChannelId)
                .HasConstraintName("fk_dlv_channel");

            entity.HasOne(d => d.ConfirmedByUser).WithMany(p => p.Deliveries)
                .HasForeignKey(d => d.ConfirmedByUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_dlv_confirmer");

            entity.HasOne(d => d.Courier).WithMany(p => p.Deliveries)
                .HasForeignKey(d => d.CourierId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_dlv_courier");

            entity.HasOne(d => d.Invoice).WithMany(p => p.Deliveries)
                .HasForeignKey(d => d.InvoiceId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_dlv_invoice");

            entity.HasOne(d => d.Order).WithMany(p => p.Deliveries)
                .HasForeignKey(d => d.OrderId)
                .HasConstraintName("fk_dlv_order");

            entity.HasOne(d => d.Status).WithMany(p => p.Deliveries)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_dlv_status");
        });

        modelBuilder.Entity<DeliveryChannel>(entity =>
        {
            entity.HasKey(e => e.ChannelId).HasName("DeliveryChannel_pkey");

            entity.ToTable("DeliveryChannel");

            entity.HasIndex(e => e.ChannelKey, "DeliveryChannel_ChannelKey_key").IsUnique();

            entity.Property(e => e.AutoConfirm).HasDefaultValue(false);
            entity.Property(e => e.ChannelKey).HasMaxLength(20);
            entity.Property(e => e.ChannelName).HasMaxLength(60);
            entity.Property(e => e.Description).HasMaxLength(200);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.RemindAfterDays).HasDefaultValue((short)0);
            entity.Property(e => e.RemindEveryHours).HasDefaultValue((short)24);
            entity.Property(e => e.RequiresBilty).HasDefaultValue(false);

            entity.HasOne(d => d.ConfirmedByRole).WithMany(p => p.DeliveryChannels)
                .HasForeignKey(d => d.ConfirmedByRoleId)
                .HasConstraintName("fk_channel_role");

            entity.HasMany(d => d.Couriers).WithMany(p => p.Channels)
                .UsingEntity<Dictionary<string, object>>(
                    "ChannelCarrier",
                    r => r.HasOne<Courier>().WithMany()
                        .HasForeignKey("CourierId")
                        .HasConstraintName("fk_cc_courier"),
                    l => l.HasOne<DeliveryChannel>().WithMany()
                        .HasForeignKey("ChannelId")
                        .HasConstraintName("fk_cc_channel"),
                    j =>
                    {
                        j.HasKey("ChannelId", "CourierId").HasName("ChannelCarrier_pkey");
                        j.ToTable("ChannelCarrier");
                    });
        });

        modelBuilder.Entity<DeliveryStatus>(entity =>
        {
            entity.HasKey(e => e.StatusId).HasName("DeliveryStatus_pkey");

            entity.ToTable("DeliveryStatus");

            entity.HasIndex(e => e.StatusKey, "DeliveryStatus_StatusKey_key").IsUnique();

            entity.Property(e => e.IsOpen).HasDefaultValue(true);
            entity.Property(e => e.StatusKey).HasMaxLength(25);
            entity.Property(e => e.StatusName).HasMaxLength(40);
        });

        modelBuilder.Entity<DocumentSeries>(entity =>
        {
            entity.HasKey(e => e.SeriesId).HasName("DocumentSeries_pkey");

            entity.HasIndex(e => e.Prefix, "DocumentSeries_Prefix_key").IsUnique();

            entity.HasIndex(e => e.SeriesKey, "DocumentSeries_SeriesKey_key").IsUnique();

            entity.Property(e => e.IncludeYear).HasDefaultValue(true);
            entity.Property(e => e.Label).HasMaxLength(60);
            entity.Property(e => e.NextNumber).HasDefaultValue(1);
            entity.Property(e => e.Padding).HasDefaultValue((short)4);
            entity.Property(e => e.Prefix).HasMaxLength(10);
            entity.Property(e => e.SeriesKey).HasMaxLength(30);
        });

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("Employee_pkey");

            entity.ToTable("Employee");

            entity.HasIndex(e => e.EmployeeCode, "Employee_EmployeeCode_key").IsUnique();

            entity.Property(e => e.UserId).ValueGeneratedNever();
            entity.Property(e => e.EmployeeCode).HasMaxLength(15);
            entity.Property(e => e.IsLocked).HasDefaultValue(false);
            entity.Property(e => e.JoinedOn).HasDefaultValueSql("CURRENT_DATE");
            entity.Property(e => e.LastLoginAt).HasColumnType("timestamp without time zone");

            entity.HasOne(d => d.User).WithOne(p => p.Employee)
                .HasForeignKey<Employee>(d => d.UserId)
                .HasConstraintName("fk_employee_user");
        });

        modelBuilder.Entity<Expense>(entity =>
        {
            entity.HasKey(e => e.ExpenseId).HasName("Expense_pkey");

            entity.ToTable("Expense");

            entity.HasIndex(e => e.ExpenseNo, "Expense_ExpenseNo_key").IsUnique();

            entity.Property(e => e.Amount).HasPrecision(14, 2);
            entity.Property(e => e.CategoryName).HasMaxLength(80);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ExpenseNo).HasMaxLength(20);
            entity.Property(e => e.VendorName).HasMaxLength(150);

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.Expenses)
                .HasForeignKey(d => d.CreatedByUserId)
                .HasConstraintName("fk_expense_created_by");

            entity.HasOne(d => d.Entry).WithMany(p => p.Expenses)
                .HasForeignKey(d => d.EntryId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_expense_entry");

            entity.HasOne(d => d.ExpenseAccount).WithMany(p => p.ExpenseExpenseAccounts)
                .HasForeignKey(d => d.ExpenseAccountId)
                .HasConstraintName("fk_expense_account");

            entity.HasOne(d => d.Location).WithMany(p => p.Expenses)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_expense_location");

            entity.HasOne(d => d.Method).WithMany(p => p.Expenses)
                .HasForeignKey(d => d.MethodId)
                .HasConstraintName("fk_expense_method");

            entity.HasOne(d => d.PaidFromAccount).WithMany(p => p.ExpensePaidFromAccounts)
                .HasForeignKey(d => d.PaidFromAccountId)
                .HasConstraintName("fk_expense_paidfrom");

            entity.HasOne(d => d.Status).WithMany(p => p.Expenses)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_expense_status");
        });

        modelBuilder.Entity<FiscalPeriod>(entity =>
        {
            entity.HasKey(e => e.PeriodId).HasName("FiscalPeriod_pkey");

            entity.ToTable("FiscalPeriod");

            entity.HasIndex(e => e.PeriodName, "FiscalPeriod_PeriodName_key").IsUnique();

            entity.HasIndex(e => new { e.PeriodYear, e.PeriodMonth }, "uq_period_year_month").IsUnique();

            entity.Property(e => e.IsClosed).HasDefaultValue(false);
            entity.Property(e => e.PeriodName).HasMaxLength(20);

            entity.HasOne(d => d.ClosedByUser).WithMany(p => p.FiscalPeriods)
                .HasForeignKey(d => d.ClosedByUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_period_closed_by");
        });

        modelBuilder.Entity<GoodsReceipt>(entity =>
        {
            entity.HasKey(e => e.GrnId).HasName("GoodsReceipt_pkey");

            entity.ToTable("GoodsReceipt");

            entity.HasIndex(e => e.GrnNo, "GoodsReceipt_GrnNo_key").IsUnique();

            entity.Property(e => e.DeliveryNoteNo).HasMaxLength(50);
            entity.Property(e => e.GrnNo).HasMaxLength(20);
            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.Property(e => e.TotalValue).HasPrecision(14, 2);
            entity.Property(e => e.VehicleNo).HasMaxLength(30);

            entity.HasOne(d => d.Entry).WithMany(p => p.GoodsReceipts)
                .HasForeignKey(d => d.EntryId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_grn_entry");

            entity.HasOne(d => d.Location).WithMany(p => p.GoodsReceipts)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_grn_location");

            entity.HasOne(d => d.Po).WithMany(p => p.GoodsReceipts)
                .HasForeignKey(d => d.PoId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_grn_po");

            entity.HasOne(d => d.ReceivedByUser).WithMany(p => p.GoodsReceipts)
                .HasForeignKey(d => d.ReceivedByUserId)
                .HasConstraintName("fk_grn_receiver");

            entity.HasOne(d => d.Status).WithMany(p => p.GoodsReceipts)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_grn_status");

            entity.HasOne(d => d.SupplierUser).WithMany(p => p.GoodsReceipts)
                .HasForeignKey(d => d.SupplierUserId)
                .HasConstraintName("fk_grn_supplier");
        });

        modelBuilder.Entity<GoodsReceiptItem>(entity =>
        {
            entity.HasKey(e => e.GrnItemId).HasName("GoodsReceiptItem_pkey");

            entity.ToTable("GoodsReceiptItem");

            entity.HasIndex(e => new { e.GrnId, e.LineNo }, "uq_grni_line").IsUnique();

            entity.HasIndex(e => new { e.GrnId, e.ProductId }, "uq_grni_product").IsUnique();

            entity.Property(e => e.BatchNo).HasMaxLength(50);
            entity.Property(e => e.QtyDamaged).HasDefaultValue(0);
            entity.Property(e => e.UnitCost).HasPrecision(14, 2);

            entity.HasOne(d => d.Grn).WithMany(p => p.GoodsReceiptItems)
                .HasForeignKey(d => d.GrnId)
                .HasConstraintName("fk_grni_grn");

            entity.HasOne(d => d.Product).WithMany(p => p.GoodsReceiptItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_grni_product");
        });

        modelBuilder.Entity<InvoiceStatus>(entity =>
        {
            entity.HasKey(e => e.StatusId).HasName("InvoiceStatus_pkey");

            entity.ToTable("InvoiceStatus");

            entity.HasIndex(e => e.StatusKey, "InvoiceStatus_StatusKey_key").IsUnique();

            entity.Property(e => e.StatusKey).HasMaxLength(20);
            entity.Property(e => e.StatusName).HasMaxLength(40);
        });

        modelBuilder.Entity<JournalEntry>(entity =>
        {
            entity.HasKey(e => e.EntryId).HasName("JournalEntry_pkey");

            entity.ToTable("JournalEntry");

            entity.HasIndex(e => e.EntryNo, "JournalEntry_EntryNo_key").IsUnique();

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_DATE");
            entity.Property(e => e.EntryNo).HasMaxLength(20);
            entity.Property(e => e.Narration).HasMaxLength(500);
            entity.Property(e => e.ReferenceNo).HasMaxLength(30);

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.JournalEntryCreatedByUsers)
                .HasForeignKey(d => d.CreatedByUserId)
                .HasConstraintName("fk_je_created_by");

            entity.HasOne(d => d.EntryType).WithMany(p => p.JournalEntries)
                .HasForeignKey(d => d.EntryTypeId)
                .HasConstraintName("fk_je_type");

            entity.HasOne(d => d.Location).WithMany(p => p.JournalEntries)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_je_location");

            entity.HasOne(d => d.Period).WithMany(p => p.JournalEntries)
                .HasForeignKey(d => d.PeriodId)
                .HasConstraintName("fk_je_period");

            entity.HasOne(d => d.PostedByUser).WithMany(p => p.JournalEntryPostedByUsers)
                .HasForeignKey(d => d.PostedByUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_je_posted_by");

            entity.HasOne(d => d.Status).WithMany(p => p.JournalEntries)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_je_status");
        });

        modelBuilder.Entity<JournalEntryLine>(entity =>
        {
            entity.HasKey(e => e.LineId).HasName("JournalEntryLine_pkey");

            entity.ToTable("JournalEntryLine");

            entity.HasIndex(e => new { e.EntryId, e.LineNo }, "uq_jel_line").IsUnique();

            entity.Property(e => e.CreditAmount).HasPrecision(14, 2);
            entity.Property(e => e.DebitAmount).HasPrecision(14, 2);
            entity.Property(e => e.Description).HasMaxLength(300);

            entity.HasOne(d => d.Account).WithMany(p => p.JournalEntryLines)
                .HasForeignKey(d => d.AccountId)
                .HasConstraintName("fk_jel_account");

            entity.HasOne(d => d.Entry).WithMany(p => p.JournalEntryLines)
                .HasForeignKey(d => d.EntryId)
                .HasConstraintName("fk_jel_entry");

            entity.HasOne(d => d.PartyUser).WithMany(p => p.JournalEntryLines)
                .HasForeignKey(d => d.PartyUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_jel_party");
        });

        modelBuilder.Entity<JournalEntryType>(entity =>
        {
            entity.HasKey(e => e.EntryTypeId).HasName("JournalEntryType_pkey");

            entity.ToTable("JournalEntryType");

            entity.HasIndex(e => e.TypeKey, "JournalEntryType_TypeKey_key").IsUnique();

            entity.Property(e => e.TypeKey).HasMaxLength(20);
            entity.Property(e => e.TypeName).HasMaxLength(40);
        });

        modelBuilder.Entity<Location>(entity =>
        {
            entity.HasKey(e => e.LocationId).HasName("Location_pkey");

            entity.ToTable("Location");

            entity.HasIndex(e => e.LocationCode, "Location_LocationCode_key").IsUnique();

            entity.HasIndex(e => e.LocationName, "Location_LocationName_key").IsUnique();

            entity.Property(e => e.AddressLine).HasMaxLength(200);
            entity.Property(e => e.ExcludeFromSellable).HasDefaultValue(false);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.IsDefault).HasDefaultValue(false);
            entity.Property(e => e.LocationCode).HasMaxLength(10);
            entity.Property(e => e.LocationName).HasMaxLength(80);

            entity.HasOne(d => d.City).WithMany(p => p.Locations)
                .HasForeignKey(d => d.CityId)
                .HasConstraintName("fk_location_city");

            entity.HasOne(d => d.InChargeUser).WithMany(p => p.Locations)
                .HasForeignKey(d => d.InChargeUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_location_incharge");

            entity.HasOne(d => d.Kind).WithMany(p => p.Locations)
                .HasForeignKey(d => d.KindId)
                .HasConstraintName("fk_location_kind");
        });

        modelBuilder.Entity<LocationKind>(entity =>
        {
            entity.HasKey(e => e.KindId).HasName("LocationKind_pkey");

            entity.ToTable("LocationKind");

            entity.HasIndex(e => e.KindKey, "LocationKind_KindKey_key").IsUnique();

            entity.Property(e => e.KindKey).HasMaxLength(20);
            entity.Property(e => e.KindName).HasMaxLength(40);
        });

        modelBuilder.Entity<MovementType>(entity =>
        {
            entity.HasKey(e => e.MovementTypeId).HasName("MovementType_pkey");

            entity.ToTable("MovementType");

            entity.HasIndex(e => e.TypeKey, "MovementType_TypeKey_key").IsUnique();

            entity.Property(e => e.TypeKey).HasMaxLength(25);
            entity.Property(e => e.TypeName).HasMaxLength(40);
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.NotificationId).HasName("Notification_pkey");

            entity.ToTable("Notification");

            entity.Property(e => e.Body).HasMaxLength(300);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp without time zone");
            entity.Property(e => e.Icon).HasMaxLength(40);
            entity.Property(e => e.IsRead).HasDefaultValue(false);
            entity.Property(e => e.Title).HasMaxLength(120);

            entity.HasOne(d => d.Severity).WithMany(p => p.Notifications)
                .HasForeignKey(d => d.SeverityId)
                .HasConstraintName("fk_notif_severity");

            entity.HasOne(d => d.User).WithMany(p => p.Notifications)
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("fk_notif_user");
        });

        modelBuilder.Entity<OrderStatus>(entity =>
        {
            entity.HasKey(e => e.StatusId).HasName("OrderStatus_pkey");

            entity.ToTable("OrderStatus");

            entity.HasIndex(e => e.StatusKey, "OrderStatus_StatusKey_key").IsUnique();

            entity.Property(e => e.StatusKey).HasMaxLength(20);
            entity.Property(e => e.StatusName).HasMaxLength(40);
        });

        modelBuilder.Entity<Party>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("Party_pkey");

            entity.ToTable("Party");

            entity.HasIndex(e => e.Cnic, "Party_Cnic_key").IsUnique();

            entity.HasIndex(e => e.Ntn, "Party_Ntn_key").IsUnique();

            entity.HasIndex(e => e.PartyCode, "Party_PartyCode_key").IsUnique();

            entity.HasIndex(e => e.Strn, "Party_Strn_key").IsUnique();

            entity.Property(e => e.UserId).ValueGeneratedNever();
            entity.Property(e => e.AddressLine).HasMaxLength(200);
            entity.Property(e => e.AltPhone).HasMaxLength(30);
            entity.Property(e => e.Cnic).HasMaxLength(20);
            entity.Property(e => e.CreditDays).HasDefaultValue(0);
            entity.Property(e => e.CreditLimit).HasPrecision(14, 2);
            entity.Property(e => e.DisplayName).HasMaxLength(150);
            entity.Property(e => e.Industry).HasMaxLength(100);
            entity.Property(e => e.LegalName).HasMaxLength(200);
            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.Property(e => e.Ntn).HasMaxLength(20);
            entity.Property(e => e.OpeningBalance).HasPrecision(14, 2);
            entity.Property(e => e.PartyCode).HasMaxLength(15);
            entity.Property(e => e.Rating)
                .HasMaxLength(1)
                .HasDefaultValueSql("'B'::bpchar");
            entity.Property(e => e.Strn).HasMaxLength(30);

            entity.HasOne(d => d.Category).WithMany(p => p.Parties)
                .HasForeignKey(d => d.CategoryId)
                .HasConstraintName("fk_party_category");

            entity.HasOne(d => d.City).WithMany(p => p.Parties)
                .HasForeignKey(d => d.CityId)
                .HasConstraintName("fk_party_city");

            entity.HasOne(d => d.DefaultLocation).WithMany(p => p.Parties)
                .HasForeignKey(d => d.DefaultLocationId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_party_location");

            entity.HasOne(d => d.HoldPolicy).WithMany(p => p.Parties)
                .HasForeignKey(d => d.HoldPolicyId)
                .HasConstraintName("fk_party_hold_policy");

            entity.HasOne(d => d.SalesPersonUser).WithMany(p => p.Parties)
                .HasForeignKey(d => d.SalesPersonUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_party_salesperson");

            entity.HasOne(d => d.User).WithOne(p => p.Party)
                .HasForeignKey<Party>(d => d.UserId)
                .HasConstraintName("fk_party_user");
        });

        modelBuilder.Entity<PartyCategory>(entity =>
        {
            entity.HasKey(e => e.CategoryId).HasName("PartyCategory_pkey");

            entity.ToTable("PartyCategory");

            entity.HasIndex(e => e.CategoryKey, "PartyCategory_CategoryKey_key").IsUnique();

            entity.Property(e => e.CategoryKey).HasMaxLength(20);
            entity.Property(e => e.CategoryName).HasMaxLength(40);
        });

        modelBuilder.Entity<PaymentMethod>(entity =>
        {
            entity.HasKey(e => e.MethodId).HasName("PaymentMethod_pkey");

            entity.ToTable("PaymentMethod");

            entity.HasIndex(e => e.MethodKey, "PaymentMethod_MethodKey_key").IsUnique();

            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.MethodKey).HasMaxLength(20);
            entity.Property(e => e.MethodKind).HasMaxLength(20);
            entity.Property(e => e.MethodName).HasMaxLength(40);
        });

        modelBuilder.Entity<Permission>(entity =>
        {
            entity.HasKey(e => e.PermissionId).HasName("Permission_pkey");

            entity.ToTable("Permission");

            entity.HasIndex(e => e.PermissionKey, "Permission_PermissionKey_key").IsUnique();

            entity.Property(e => e.GroupName).HasMaxLength(40);
            entity.Property(e => e.Label).HasMaxLength(80);
            entity.Property(e => e.PermissionKey).HasMaxLength(40);
        });

        modelBuilder.Entity<PostingStatus>(entity =>
        {
            entity.HasKey(e => e.StatusId).HasName("PostingStatus_pkey");

            entity.ToTable("PostingStatus");

            entity.HasIndex(e => e.StatusKey, "PostingStatus_StatusKey_key").IsUnique();

            entity.Property(e => e.StatusKey).HasMaxLength(20);
            entity.Property(e => e.StatusName).HasMaxLength(40);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.ProductId).HasName("Product_pkey");

            entity.ToTable("Product");

            entity.HasIndex(e => e.Sku, "Product_Sku_key").IsUnique();

            entity.Property(e => e.CostPrice).HasPrecision(14, 2);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_DATE");
            entity.Property(e => e.Description).HasMaxLength(300);
            entity.Property(e => e.HideStock).HasDefaultValue(false);
            entity.Property(e => e.ImageUrl).HasMaxLength(300);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.MaxQty).HasDefaultValue(0);
            entity.Property(e => e.MinQty).HasDefaultValue(0);
            entity.Property(e => e.OpeningCost).HasPrecision(14, 2);
            entity.Property(e => e.Packing).HasDefaultValue(1);
            entity.Property(e => e.ProductName).HasMaxLength(150);
            entity.Property(e => e.SalePrice).HasPrecision(14, 2);
            entity.Property(e => e.Sku).HasMaxLength(30);
            entity.Property(e => e.TaxRatePercent).HasPrecision(5, 2);

            entity.HasOne(d => d.Brand).WithMany(p => p.Products)
                .HasForeignKey(d => d.BrandId)
                .HasConstraintName("fk_product_brand");

            entity.HasOne(d => d.Category).WithMany(p => p.Products)
                .HasForeignKey(d => d.CategoryId)
                .HasConstraintName("fk_product_category");
        });

        modelBuilder.Entity<ProductBarcode>(entity =>
        {
            entity.HasKey(e => e.BarcodeId).HasName("ProductBarcode_pkey");

            entity.ToTable("ProductBarcode");

            entity.HasIndex(e => e.Barcode, "ProductBarcode_Barcode_key").IsUnique();

            entity.Property(e => e.Barcode).HasMaxLength(40);

            entity.HasOne(d => d.Product).WithMany(p => p.ProductBarcodes)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_barcode_product");
        });

        modelBuilder.Entity<Province>(entity =>
        {
            entity.HasKey(e => e.ProvinceId).HasName("Province_pkey");

            entity.ToTable("Province");

            entity.HasIndex(e => e.ProvinceName, "Province_ProvinceName_key").IsUnique();

            entity.Property(e => e.ProvinceName).HasMaxLength(60);
        });

        modelBuilder.Entity<PurchaseInvoice>(entity =>
        {
            entity.HasKey(e => e.PiId).HasName("PurchaseInvoice_pkey");

            entity.ToTable("PurchaseInvoice");

            entity.HasIndex(e => e.InvoiceNo, "PurchaseInvoice_InvoiceNo_key").IsUnique();

            entity.HasIndex(e => new { e.SupplierUserId, e.SupplierInvoiceNo }, "uq_pi_supplier_ref").IsUnique();

            entity.Property(e => e.DiscountAmount).HasPrecision(14, 2);
            entity.Property(e => e.InvoiceNo).HasMaxLength(20);
            entity.Property(e => e.Subtotal).HasPrecision(14, 2);
            entity.Property(e => e.SupplierInvoiceNo).HasMaxLength(50);
            entity.Property(e => e.TaxAmount).HasPrecision(14, 2);
            entity.Property(e => e.TotalAmount).HasPrecision(14, 2);
            entity.Property(e => e.WhtAmount).HasPrecision(14, 2);

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.PurchaseInvoices)
                .HasForeignKey(d => d.CreatedByUserId)
                .HasConstraintName("fk_pi_created_by");

            entity.HasOne(d => d.Entry).WithMany(p => p.PurchaseInvoices)
                .HasForeignKey(d => d.EntryId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_pi_entry");

            entity.HasOne(d => d.Method).WithMany(p => p.PurchaseInvoices)
                .HasForeignKey(d => d.MethodId)
                .HasConstraintName("fk_pi_method");

            entity.HasOne(d => d.Po).WithMany(p => p.PurchaseInvoices)
                .HasForeignKey(d => d.PoId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_pi_po");

            entity.HasOne(d => d.Status).WithMany(p => p.PurchaseInvoices)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_pi_status");

            entity.HasOne(d => d.SupplierUser).WithMany(p => p.PurchaseInvoices)
                .HasForeignKey(d => d.SupplierUserId)
                .HasConstraintName("fk_pi_supplier");
        });

        modelBuilder.Entity<PurchaseInvoiceItem>(entity =>
        {
            entity.HasKey(e => e.PiItemId).HasName("PurchaseInvoiceItem_pkey");

            entity.ToTable("PurchaseInvoiceItem");

            entity.HasIndex(e => new { e.PiId, e.LineNo }, "uq_pii_line").IsUnique();

            entity.HasIndex(e => new { e.PiId, e.ProductId }, "uq_pii_product").IsUnique();

            entity.Property(e => e.LineTotal).HasPrecision(14, 2);
            entity.Property(e => e.TaxPercent).HasPrecision(5, 2);
            entity.Property(e => e.UnitCost).HasPrecision(14, 2);

            entity.HasOne(d => d.Pi).WithMany(p => p.PurchaseInvoiceItems)
                .HasForeignKey(d => d.PiId)
                .HasConstraintName("fk_pii_pi");

            entity.HasOne(d => d.Product).WithMany(p => p.PurchaseInvoiceItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_pii_product");
        });

        modelBuilder.Entity<PurchaseOrder>(entity =>
        {
            entity.HasKey(e => e.PoId).HasName("PurchaseOrder_pkey");

            entity.ToTable("PurchaseOrder");

            entity.HasIndex(e => e.PoNo, "PurchaseOrder_PoNo_key").IsUnique();

            entity.Property(e => e.DiscountAmount).HasPrecision(14, 2);
            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.Property(e => e.PoNo).HasMaxLength(20);
            entity.Property(e => e.Subtotal).HasPrecision(14, 2);
            entity.Property(e => e.TaxAmount).HasPrecision(14, 2);
            entity.Property(e => e.TotalAmount).HasPrecision(14, 2);

            entity.HasOne(d => d.ApprovedByUser).WithMany(p => p.PurchaseOrderApprovedByUsers)
                .HasForeignKey(d => d.ApprovedByUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_po_approved_by");

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.PurchaseOrderCreatedByUsers)
                .HasForeignKey(d => d.CreatedByUserId)
                .HasConstraintName("fk_po_created_by");

            entity.HasOne(d => d.Location).WithMany(p => p.PurchaseOrders)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_po_location");

            entity.HasOne(d => d.Status).WithMany(p => p.PurchaseOrders)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_po_status");

            entity.HasOne(d => d.SupplierUser).WithMany(p => p.PurchaseOrders)
                .HasForeignKey(d => d.SupplierUserId)
                .HasConstraintName("fk_po_supplier");
        });

        modelBuilder.Entity<PurchaseOrderItem>(entity =>
        {
            entity.HasKey(e => e.PoItemId).HasName("PurchaseOrderItem_pkey");

            entity.ToTable("PurchaseOrderItem");

            entity.HasIndex(e => new { e.PoId, e.LineNo }, "uq_poi_line").IsUnique();

            entity.HasIndex(e => new { e.PoId, e.ProductId }, "uq_poi_product").IsUnique();

            entity.Property(e => e.LineTotal).HasPrecision(14, 2);
            entity.Property(e => e.TaxPercent).HasPrecision(5, 2);
            entity.Property(e => e.UnitCost).HasPrecision(14, 2);

            entity.HasOne(d => d.Po).WithMany(p => p.PurchaseOrderItems)
                .HasForeignKey(d => d.PoId)
                .HasConstraintName("fk_poi_po");

            entity.HasOne(d => d.Product).WithMany(p => p.PurchaseOrderItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_poi_product");
        });

        modelBuilder.Entity<PurchaseOrderStatus>(entity =>
        {
            entity.HasKey(e => e.StatusId).HasName("PurchaseOrderStatus_pkey");

            entity.ToTable("PurchaseOrderStatus");

            entity.HasIndex(e => e.StatusKey, "PurchaseOrderStatus_StatusKey_key").IsUnique();

            entity.Property(e => e.StatusKey).HasMaxLength(25);
            entity.Property(e => e.StatusName).HasMaxLength(40);
        });

        modelBuilder.Entity<PurchaseReturn>(entity =>
        {
            entity.HasKey(e => e.PrId).HasName("PurchaseReturn_pkey");

            entity.ToTable("PurchaseReturn");

            entity.HasIndex(e => e.ReturnNo, "PurchaseReturn_ReturnNo_key").IsUnique();

            entity.Property(e => e.Reason).HasMaxLength(300);
            entity.Property(e => e.ReturnNo).HasMaxLength(20);

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.PurchaseReturns)
                .HasForeignKey(d => d.CreatedByUserId)
                .HasConstraintName("fk_pr_created_by");

            entity.HasOne(d => d.Entry).WithMany(p => p.PurchaseReturns)
                .HasForeignKey(d => d.EntryId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_pr_entry");

            entity.HasOne(d => d.Location).WithMany(p => p.PurchaseReturns)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_pr_location");

            entity.HasOne(d => d.Pi).WithMany(p => p.PurchaseReturns)
                .HasForeignKey(d => d.PiId)
                .HasConstraintName("fk_pr_pi");

            entity.HasOne(d => d.Status).WithMany(p => p.PurchaseReturns)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_pr_status");

            entity.HasOne(d => d.SupplierUser).WithMany(p => p.PurchaseReturns)
                .HasForeignKey(d => d.SupplierUserId)
                .HasConstraintName("fk_pr_supplier");
        });

        modelBuilder.Entity<PurchaseReturnItem>(entity =>
        {
            entity.HasKey(e => e.PrItemId).HasName("PurchaseReturnItem_pkey");

            entity.ToTable("PurchaseReturnItem");

            entity.HasIndex(e => new { e.PrId, e.LineNo }, "uq_pri_line").IsUnique();

            entity.Property(e => e.UnitCost).HasPrecision(14, 2);

            entity.HasOne(d => d.Pr).WithMany(p => p.PurchaseReturnItems)
                .HasForeignKey(d => d.PrId)
                .HasConstraintName("fk_pri_pr");

            entity.HasOne(d => d.Product).WithMany(p => p.PurchaseReturnItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_pri_product");
        });

        modelBuilder.Entity<ReturnCondition>(entity =>
        {
            entity.HasKey(e => e.ConditionId).HasName("ReturnCondition_pkey");

            entity.ToTable("ReturnCondition");

            entity.HasIndex(e => e.ConditionKey, "ReturnCondition_ConditionKey_key").IsUnique();

            entity.Property(e => e.ConditionKey).HasMaxLength(20);
            entity.Property(e => e.ConditionName).HasMaxLength(40);
            entity.Property(e => e.IsResalable).HasDefaultValue(false);
        });

        modelBuilder.Entity<ReturnStatus>(entity =>
        {
            entity.HasKey(e => e.StatusId).HasName("ReturnStatus_pkey");

            entity.ToTable("ReturnStatus");

            entity.HasIndex(e => e.StatusKey, "ReturnStatus_StatusKey_key").IsUnique();

            entity.Property(e => e.StatusKey).HasMaxLength(20);
            entity.Property(e => e.StatusName).HasMaxLength(40);
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.RoleId).HasName("Role_pkey");

            entity.ToTable("Role");

            entity.HasIndex(e => e.RoleKey, "Role_RoleKey_key").IsUnique();

            entity.HasIndex(e => new { e.RoleId, e.RequiresEmail }, "uq_role_email_rule").IsUnique();

            entity.Property(e => e.Description).HasMaxLength(200);
            entity.Property(e => e.HomePath)
                .HasMaxLength(100)
                .HasDefaultValueSql("'/dashboard'::character varying");
            entity.Property(e => e.IsStaffRole)
                .HasDefaultValue(true)
                .ValueGeneratedNever();

            entity.Property(e => e.IsSystem)
                .HasDefaultValue(true)
                .ValueGeneratedNever();

            entity.Property(e => e.RequiresEmail)
                .HasDefaultValue(true)
                .ValueGeneratedNever();
            entity.Property(e => e.RoleKey).HasMaxLength(30);
            entity.Property(e => e.RoleName).HasMaxLength(60);

            entity.HasMany(d => d.Permissions).WithMany(p => p.Roles)
                .UsingEntity<Dictionary<string, object>>(
                    "RolePermission",
                    r => r.HasOne<Permission>().WithMany()
                        .HasForeignKey("PermissionId")
                        .HasConstraintName("fk_rp_permission"),
                    l => l.HasOne<Role>().WithMany()
                        .HasForeignKey("RoleId")
                        .HasConstraintName("fk_rp_role"),
                    j =>
                    {
                        j.HasKey("RoleId", "PermissionId").HasName("RolePermission_pkey");
                        j.ToTable("RolePermission");
                    });
        });

        modelBuilder.Entity<SalesInvoice>(entity =>
        {
            entity.HasKey(e => e.InvoiceId).HasName("SalesInvoice_pkey");

            entity.ToTable("SalesInvoice");

            entity.HasIndex(e => e.InvoiceNo, "SalesInvoice_InvoiceNo_key").IsUnique();

            entity.HasIndex(e => e.OrderId, "SalesInvoice_OrderId_key").IsUnique();

            entity.Property(e => e.DiscountAmount).HasPrecision(14, 2);
            entity.Property(e => e.InvoiceNo).HasMaxLength(20);
            entity.Property(e => e.Subtotal).HasPrecision(14, 2);
            entity.Property(e => e.TaxAmount).HasPrecision(14, 2);
            entity.Property(e => e.TotalAmount).HasPrecision(14, 2);

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.SalesInvoices)
                .HasForeignKey(d => d.CreatedByUserId)
                .HasConstraintName("fk_si_created_by");

            entity.HasOne(d => d.CustomerUser).WithMany(p => p.SalesInvoices)
                .HasForeignKey(d => d.CustomerUserId)
                .HasConstraintName("fk_si_customer");

            entity.HasOne(d => d.Entry).WithMany(p => p.SalesInvoices)
                .HasForeignKey(d => d.EntryId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_si_entry");

            entity.HasOne(d => d.Location).WithMany(p => p.SalesInvoices)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_si_location");

            entity.HasOne(d => d.Method).WithMany(p => p.SalesInvoices)
                .HasForeignKey(d => d.MethodId)
                .HasConstraintName("fk_si_method");

            entity.HasOne(d => d.Order).WithOne(p => p.SalesInvoice)
                .HasForeignKey<SalesInvoice>(d => d.OrderId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_si_order");

            entity.HasOne(d => d.Status).WithMany(p => p.SalesInvoices)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_si_status");
        });

        modelBuilder.Entity<SalesInvoiceItem>(entity =>
        {
            entity.HasKey(e => e.InvoiceItemId).HasName("SalesInvoiceItem_pkey");

            entity.ToTable("SalesInvoiceItem");

            entity.HasIndex(e => new { e.InvoiceId, e.LineNo }, "uq_sii_line").IsUnique();

            entity.HasIndex(e => new { e.InvoiceId, e.ProductId }, "uq_sii_product").IsUnique();

            entity.Property(e => e.DiscountPercent).HasPrecision(5, 2);
            entity.Property(e => e.LineTotal).HasPrecision(14, 2);
            entity.Property(e => e.TaxPercent).HasPrecision(5, 2);
            entity.Property(e => e.UnitCost).HasPrecision(14, 2);
            entity.Property(e => e.UnitPrice).HasPrecision(14, 2);

            entity.HasOne(d => d.Invoice).WithMany(p => p.SalesInvoiceItems)
                .HasForeignKey(d => d.InvoiceId)
                .HasConstraintName("fk_sii_invoice");

            entity.HasOne(d => d.Product).WithMany(p => p.SalesInvoiceItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_sii_product");
        });

        modelBuilder.Entity<SalesOrder>(entity =>
        {
            entity.HasKey(e => e.OrderId).HasName("SalesOrder_pkey");

            entity.ToTable("SalesOrder");

            entity.HasIndex(e => e.OrderNo, "SalesOrder_OrderNo_key").IsUnique();

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_DATE");
            entity.Property(e => e.CreditHoldReason).HasMaxLength(300);
            entity.Property(e => e.DiscountAmount).HasPrecision(14, 2);
            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.Property(e => e.OrderNo).HasMaxLength(20);
            entity.Property(e => e.Subtotal).HasPrecision(14, 2);
            entity.Property(e => e.TaxAmount).HasPrecision(14, 2);
            entity.Property(e => e.TotalAmount).HasPrecision(14, 2);

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.SalesOrders)
                .HasForeignKey(d => d.CreatedByUserId)
                .HasConstraintName("fk_order_created_by");

            entity.HasOne(d => d.CustomerUser).WithMany(p => p.SalesOrders)
                .HasForeignKey(d => d.CustomerUserId)
                .HasConstraintName("fk_order_customer");

            entity.HasOne(d => d.Location).WithMany(p => p.SalesOrders)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_order_location");

            entity.HasOne(d => d.Method).WithMany(p => p.SalesOrders)
                .HasForeignKey(d => d.MethodId)
                .HasConstraintName("fk_order_method");

            entity.HasOne(d => d.SalesPersonUser).WithMany(p => p.SalesOrders)
                .HasForeignKey(d => d.SalesPersonUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_order_salesperson");

            entity.HasOne(d => d.Status).WithMany(p => p.SalesOrders)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_order_status");
        });

        modelBuilder.Entity<SalesOrderItem>(entity =>
        {
            entity.HasKey(e => e.OrderItemId).HasName("SalesOrderItem_pkey");

            entity.ToTable("SalesOrderItem");

            entity.HasIndex(e => new { e.OrderId, e.LineNo }, "uq_soi_line").IsUnique();

            entity.HasIndex(e => new { e.OrderId, e.ProductId }, "uq_soi_product").IsUnique();

            entity.Property(e => e.DiscountPercent).HasPrecision(5, 2);
            entity.Property(e => e.LineTotal).HasPrecision(14, 2);
            entity.Property(e => e.TaxPercent).HasPrecision(5, 2);
            entity.Property(e => e.UnitPrice).HasPrecision(14, 2);

            entity.HasOne(d => d.Order).WithMany(p => p.SalesOrderItems)
                .HasForeignKey(d => d.OrderId)
                .HasConstraintName("fk_soi_order");

            entity.HasOne(d => d.Product).WithMany(p => p.SalesOrderItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_soi_product");
        });

        modelBuilder.Entity<SalesReturn>(entity =>
        {
            entity.HasKey(e => e.ReturnId).HasName("SalesReturn_pkey");

            entity.ToTable("SalesReturn");

            entity.HasIndex(e => e.ReturnNo, "SalesReturn_ReturnNo_key").IsUnique();

            entity.Property(e => e.Reason).HasMaxLength(300);
            entity.Property(e => e.ReturnNo).HasMaxLength(20);

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.SalesReturns)
                .HasForeignKey(d => d.CreatedByUserId)
                .HasConstraintName("fk_sr_created_by");

            entity.HasOne(d => d.CustomerUser).WithMany(p => p.SalesReturns)
                .HasForeignKey(d => d.CustomerUserId)
                .HasConstraintName("fk_sr_customer");

            entity.HasOne(d => d.Entry).WithMany(p => p.SalesReturns)
                .HasForeignKey(d => d.EntryId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_sr_entry");

            entity.HasOne(d => d.Invoice).WithMany(p => p.SalesReturns)
                .HasForeignKey(d => d.InvoiceId)
                .HasConstraintName("fk_sr_invoice");

            entity.HasOne(d => d.Location).WithMany(p => p.SalesReturns)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_sr_location");

            entity.HasOne(d => d.RefundMethod).WithMany(p => p.SalesReturns)
                .HasForeignKey(d => d.RefundMethodId)
                .HasConstraintName("fk_sr_method");

            entity.HasOne(d => d.Status).WithMany(p => p.SalesReturns)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_sr_status");
        });

        modelBuilder.Entity<SalesReturnItem>(entity =>
        {
            entity.HasKey(e => e.ReturnItemId).HasName("SalesReturnItem_pkey");

            entity.ToTable("SalesReturnItem");

            entity.HasIndex(e => new { e.ReturnId, e.LineNo }, "uq_sri_line").IsUnique();

            entity.Property(e => e.UnitPrice).HasPrecision(14, 2);

            entity.HasOne(d => d.Condition).WithMany(p => p.SalesReturnItems)
                .HasForeignKey(d => d.ConditionId)
                .HasConstraintName("fk_sri_condition");

            entity.HasOne(d => d.Product).WithMany(p => p.SalesReturnItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_sri_product");

            entity.HasOne(d => d.RestockLocation).WithMany(p => p.SalesReturnItems)
                .HasForeignKey(d => d.RestockLocationId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_sri_location");

            entity.HasOne(d => d.Return).WithMany(p => p.SalesReturnItems)
                .HasForeignKey(d => d.ReturnId)
                .HasConstraintName("fk_sri_return");
        });

        modelBuilder.Entity<SeverityLevel>(entity =>
        {
            entity.HasKey(e => e.SeverityId).HasName("SeverityLevel_pkey");

            entity.ToTable("SeverityLevel");

            entity.HasIndex(e => e.SeverityKey, "SeverityLevel_SeverityKey_key").IsUnique();

            entity.Property(e => e.SeverityKey).HasMaxLength(20);
            entity.Property(e => e.SeverityName).HasMaxLength(40);
        });

        modelBuilder.Entity<StockAdjustment>(entity =>
        {
            entity.HasKey(e => e.AdjustmentId).HasName("StockAdjustment_pkey");

            entity.ToTable("StockAdjustment");

            entity.HasIndex(e => e.AdjustmentNo, "StockAdjustment_AdjustmentNo_key").IsUnique();

            entity.Property(e => e.AdjustmentNo).HasMaxLength(20);
            entity.Property(e => e.ReasonNotes).HasMaxLength(500);

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.StockAdjustments)
                .HasForeignKey(d => d.CreatedByUserId)
                .HasConstraintName("fk_adj_created_by");

            entity.HasOne(d => d.Entry).WithMany(p => p.StockAdjustments)
                .HasForeignKey(d => d.EntryId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_adj_entry");

            entity.HasOne(d => d.Location).WithMany(p => p.StockAdjustments)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_adj_location");

            entity.HasOne(d => d.Reason).WithMany(p => p.StockAdjustments)
                .HasForeignKey(d => d.ReasonId)
                .HasConstraintName("fk_adj_reason");

            entity.HasOne(d => d.Status).WithMany(p => p.StockAdjustments)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_adj_status");
        });

        modelBuilder.Entity<StockAdjustmentItem>(entity =>
        {
            entity.HasKey(e => e.AdjustmentItemId).HasName("StockAdjustmentItem_pkey");

            entity.ToTable("StockAdjustmentItem");

            entity.HasIndex(e => new { e.AdjustmentId, e.LineNo }, "uq_adji_line").IsUnique();

            entity.HasIndex(e => new { e.AdjustmentId, e.ProductId }, "uq_adji_product").IsUnique();

            entity.HasOne(d => d.Adjustment).WithMany(p => p.StockAdjustmentItems)
                .HasForeignKey(d => d.AdjustmentId)
                .HasConstraintName("fk_adji_adjustment");

            entity.HasOne(d => d.Product).WithMany(p => p.StockAdjustmentItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_adji_product");
        });

        modelBuilder.Entity<StockBalance>(entity =>
        {
            entity.HasKey(e => new { e.ProductId, e.LocationId }).HasName("StockBalance_pkey");

            entity.ToTable("StockBalance");

            entity.Property(e => e.Quantity).HasDefaultValue(0);

            entity.HasOne(d => d.Location).WithMany(p => p.StockBalances)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_sb_location");

            entity.HasOne(d => d.Product).WithMany(p => p.StockBalances)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_sb_product");
        });

        modelBuilder.Entity<StockMovement>(entity =>
        {
            entity.HasKey(e => e.MovementId).HasName("StockMovement_pkey");

            entity.ToTable("StockMovement");

            entity.Property(e => e.MovedAt).HasColumnType("timestamp without time zone");
            entity.Property(e => e.ReferenceNo).HasMaxLength(30);

            entity.HasOne(d => d.Location).WithMany(p => p.StockMovements)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_sm_location");

            entity.HasOne(d => d.MovementType).WithMany(p => p.StockMovements)
                .HasForeignKey(d => d.MovementTypeId)
                .HasConstraintName("fk_sm_type");

            entity.HasOne(d => d.Product).WithMany(p => p.StockMovements)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_sm_product");

            entity.HasOne(d => d.User).WithMany(p => p.StockMovements)
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("fk_sm_user");
        });

        modelBuilder.Entity<StockTransfer>(entity =>
        {
            entity.HasKey(e => e.TransferId).HasName("StockTransfer_pkey");

            entity.ToTable("StockTransfer");

            entity.HasIndex(e => e.TransferNo, "StockTransfer_TransferNo_key").IsUnique();

            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.Property(e => e.TransferNo).HasMaxLength(20);

            entity.HasOne(d => d.ApprovedByUser).WithMany(p => p.StockTransferApprovedByUsers)
                .HasForeignKey(d => d.ApprovedByUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_trf_approver");

            entity.HasOne(d => d.FromLocation).WithMany(p => p.StockTransferFromLocations)
                .HasForeignKey(d => d.FromLocationId)
                .HasConstraintName("fk_trf_from");

            entity.HasOne(d => d.InitiatedByUser).WithMany(p => p.StockTransferInitiatedByUsers)
                .HasForeignKey(d => d.InitiatedByUserId)
                .HasConstraintName("fk_trf_initiator");

            entity.HasOne(d => d.Status).WithMany(p => p.StockTransfers)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_trf_status");

            entity.HasOne(d => d.ToLocation).WithMany(p => p.StockTransferToLocations)
                .HasForeignKey(d => d.ToLocationId)
                .HasConstraintName("fk_trf_to");
        });

        modelBuilder.Entity<StockTransferItem>(entity =>
        {
            entity.HasKey(e => e.TransferItemId).HasName("StockTransferItem_pkey");

            entity.ToTable("StockTransferItem");

            entity.HasIndex(e => new { e.TransferId, e.LineNo }, "uq_sti_line").IsUnique();

            entity.HasIndex(e => new { e.TransferId, e.ProductId }, "uq_sti_product").IsUnique();

            entity.HasOne(d => d.Product).WithMany(p => p.StockTransferItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("fk_sti_product");

            entity.HasOne(d => d.Transfer).WithMany(p => p.StockTransferItems)
                .HasForeignKey(d => d.TransferId)
                .HasConstraintName("fk_sti_transfer");
        });

        modelBuilder.Entity<TransferStatus>(entity =>
        {
            entity.HasKey(e => e.StatusId).HasName("TransferStatus_pkey");

            entity.ToTable("TransferStatus");

            entity.HasIndex(e => e.StatusKey, "TransferStatus_StatusKey_key").IsUnique();

            entity.Property(e => e.StatusKey).HasMaxLength(25);
            entity.Property(e => e.StatusName).HasMaxLength(40);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("User_pkey");

            entity.ToTable("User");

            entity.HasIndex(e => e.Email, "User_Email_key").IsUnique();

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_DATE");
            entity.Property(e => e.Email).HasMaxLength(120);
            entity.Property(e => e.FullName).HasMaxLength(120);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.PasswordHash).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(30);

            entity.HasOne(d => d.PrimaryLocation).WithMany(p => p.Users)
                .HasForeignKey(d => d.PrimaryLocationId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_user_location");

            entity.HasOne(d => d.Role).WithMany(p => p.UserRoles)
                .HasForeignKey(d => d.RoleId)
                .HasConstraintName("fk_user_role");

            entity.HasOne(d => d.RoleNavigation).WithMany(p => p.UserRoleNavigations)
                .HasPrincipalKey(p => new { p.RoleId, p.RequiresEmail })
                .HasForeignKey(d => new { d.RoleId, d.RequiresEmail })
                .HasConstraintName("fk_user_email_rule");

            entity.HasMany(d => d.LocationsNavigation).WithMany(p => p.UsersNavigation)
                .UsingEntity<Dictionary<string, object>>(
                    "UserLocation",
                    r => r.HasOne<Location>().WithMany()
                        .HasForeignKey("LocationId")
                        .HasConstraintName("fk_ul_location"),
                    l => l.HasOne<User>().WithMany()
                        .HasForeignKey("UserId")
                        .HasConstraintName("fk_ul_user"),
                    j =>
                    {
                        j.HasKey("UserId", "LocationId").HasName("UserLocation_pkey");
                        j.ToTable("UserLocation");
                    });
        });

        modelBuilder.Entity<UserPreference>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.PrefKey }).HasName("UserPreference_pkey");

            entity.ToTable("UserPreference");

            entity.Property(e => e.PrefKey).HasMaxLength(40);
            entity.Property(e => e.PrefValue).HasMaxLength(100);

            entity.HasOne(d => d.User).WithMany(p => p.UserPreferences)
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("fk_pref_user");
        });

        modelBuilder.Entity<VisitOutcome>(entity =>
        {
            entity.HasKey(e => e.OutcomeId).HasName("VisitOutcome_pkey");

            entity.ToTable("VisitOutcome");

            entity.HasIndex(e => e.OutcomeKey, "VisitOutcome_OutcomeKey_key").IsUnique();

            entity.Property(e => e.OutcomeKey).HasMaxLength(25);
            entity.Property(e => e.OutcomeName).HasMaxLength(40);
        });

        modelBuilder.Entity<Voucher>(entity =>
        {
            entity.HasKey(e => e.VoucherId).HasName("Voucher_pkey");

            entity.ToTable("Voucher");

            entity.HasIndex(e => e.VoucherNo, "Voucher_VoucherNo_key").IsUnique();

            entity.Property(e => e.Amount).HasPrecision(14, 2);
            entity.Property(e => e.Narration).HasMaxLength(300);
            entity.Property(e => e.PaymentProvider).HasMaxLength(60);
            entity.Property(e => e.ReferenceNo).HasMaxLength(50);
            entity.Property(e => e.VoucherNo).HasMaxLength(20);
            entity.Property(e => e.WalletTxnId).HasMaxLength(50);

            entity.HasOne(d => d.CashBankAccount).WithMany(p => p.Vouchers)
                .HasForeignKey(d => d.CashBankAccountId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_voucher_account");

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.Vouchers)
                .HasForeignKey(d => d.CreatedByUserId)
                .HasConstraintName("fk_voucher_created_by");

            entity.HasOne(d => d.Entry).WithMany(p => p.Vouchers)
                .HasForeignKey(d => d.EntryId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_voucher_entry");

            entity.HasOne(d => d.Location).WithMany(p => p.Vouchers)
                .HasForeignKey(d => d.LocationId)
                .HasConstraintName("fk_voucher_location");

            entity.HasOne(d => d.Method).WithMany(p => p.Vouchers)
                .HasForeignKey(d => d.MethodId)
                .HasConstraintName("fk_voucher_method");

            entity.HasOne(d => d.PartyUser).WithMany(p => p.Vouchers)
                .HasForeignKey(d => d.PartyUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_voucher_party");

            entity.HasOne(d => d.Status).WithMany(p => p.Vouchers)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("fk_voucher_status");

            entity.HasOne(d => d.VoucherType).WithMany(p => p.Vouchers)
                .HasForeignKey(d => d.VoucherTypeId)
                .HasConstraintName("fk_voucher_type");
        });

        modelBuilder.Entity<VoucherAllocation>(entity =>
        {
            entity.HasKey(e => e.AllocationId).HasName("VoucherAllocation_pkey");

            entity.ToTable("VoucherAllocation");

            entity.Property(e => e.Amount).HasPrecision(14, 2);

            entity.HasOne(d => d.PurchaseInvoice).WithMany(p => p.VoucherAllocations)
                .HasForeignKey(d => d.PurchaseInvoiceId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_alloc_purchase_invoice");

            entity.HasOne(d => d.SalesInvoice).WithMany(p => p.VoucherAllocations)
                .HasForeignKey(d => d.SalesInvoiceId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_alloc_sales_invoice");

            entity.HasOne(d => d.Voucher).WithMany(p => p.VoucherAllocations)
                .HasForeignKey(d => d.VoucherId)
                .HasConstraintName("fk_alloc_voucher");
        });

        modelBuilder.Entity<VoucherType>(entity =>
        {
            entity.HasKey(e => e.VoucherTypeId).HasName("VoucherType_pkey");

            entity.ToTable("VoucherType");

            entity.HasIndex(e => e.TypeCode, "VoucherType_TypeCode_key").IsUnique();

            entity.Property(e => e.TypeCode).HasMaxLength(4);
            entity.Property(e => e.TypeName).HasMaxLength(40);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
