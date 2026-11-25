using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;

namespace ERP.Web.Data;

public class ErpDbContext : DbContext
{
    public ErpDbContext(DbContextOptions<ErpDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    // Kế toán tổng hợp
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<FiscalPeriod> FiscalPeriods => Set<FiscalPeriod>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    // Đối tác
    public DbSet<Partner> Partners => Set<Partner>();
    // Kho
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
    public DbSet<StockMove> StockMoves => Set<StockMove>();
    public DbSet<InventoryDoc> InventoryDocs => Set<InventoryDoc>();
    public DbSet<InventoryDocLine> InventoryDocLines => Set<InventoryDocLine>();
    // Mua / bán / hóa đơn
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    // Tiền
    public DbSet<CashAccount> CashAccounts => Set<CashAccount>();
    public DbSet<CashVoucher> CashVouchers => Set<CashVoucher>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    // Tài sản
    public DbSet<AssetCategory> AssetCategories => Set<AssetCategory>();
    public DbSet<FixedAsset> FixedAssets => Set<FixedAsset>();
    public DbSet<DepreciationRun> DepreciationRuns => Set<DepreciationRun>();
    public DbSet<DepreciationLine> DepreciationLines => Set<DepreciationLine>();
    // Sản xuất
    public DbSet<Bom> Boms => Set<Bom>();
    public DbSet<BomLine> BomLines => Set<BomLine>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<ProductionOrderLine> ProductionOrderLines => Set<ProductionOrderLine>();
    // Danh mục dùng chung & tùy chọn người dùng
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    // Cấu hình bút toán theo giao dịch
    public DbSet<PostingRule> PostingRules => Set<PostingRule>();
    // Quản lý chất lượng
    public DbSet<QcPlan> QcPlans => Set<QcPlan>();
    public DbSet<QcPlanLine> QcPlanLines => Set<QcPlanLine>();
    public DbSet<QcInspection> QcInspections => Set<QcInspection>();
    public DbSet<QcInspectionLine> QcInspectionLines => Set<QcInspectionLine>();
    public DbSet<QcNonconformance> QcNonconformances => Set<QcNonconformance>();
    public DbSet<PurchaseReturn> PurchaseReturns => Set<PurchaseReturn>();
    public DbSet<InvoiceOffset> InvoiceOffsets => Set<InvoiceOffset>();
    public DbSet<SalesCreditNote> SalesCreditNotes => Set<SalesCreditNote>();
    public DbSet<SalesCreditLine> SalesCreditLines => Set<SalesCreditLine>();
    public DbSet<SalesCreditOffset> SalesCreditOffsets => Set<SalesCreditOffset>();

    /// <summary>Mọi cột tiền / số lượng: decimal(18,2)</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<Account>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<FiscalPeriod>().HasIndex(x => new { x.Year, x.Month }).IsUnique();
        modelBuilder.Entity<JournalEntry>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<JournalEntry>().HasIndex(x => new { x.Date, x.Status });
        modelBuilder.Entity<JournalLine>().HasIndex(x => new { x.AccountId, x.PartnerId });
        modelBuilder.Entity<Partner>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Item>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Warehouse>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<StockLevel>().HasIndex(x => new { x.ItemId, x.WarehouseId }).IsUnique();
        modelBuilder.Entity<StockMove>().HasIndex(x => new { x.ItemId, x.Date });
        modelBuilder.Entity<InventoryDoc>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<PurchaseOrder>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<SalesOrder>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Invoice>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Invoice>().HasIndex(x => new { x.Direction, x.PartnerId, x.Status });
        modelBuilder.Entity<CashAccount>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<CashVoucher>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<AssetCategory>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<FixedAsset>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<DepreciationRun>().HasIndex(x => new { x.Year, x.Month }).IsUnique();
        modelBuilder.Entity<Bom>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<ProductionOrder>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<CatalogItem>().HasIndex(x => new { x.Type, x.Code }).IsUnique();
        modelBuilder.Entity<UserPreference>().HasIndex(x => new { x.UserId, x.Key }).IsUnique();
        modelBuilder.Entity<PostingRule>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<QcPlan>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<QcInspection>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<QcInspection>().HasIndex(x => new { x.Origin, x.OriginId });
        modelBuilder.Entity<QcNonconformance>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<PurchaseReturn>().HasIndex(x => x.NcrId);
        modelBuilder.Entity<PurchaseReturn>().HasIndex(x => x.PurchaseOrderLineId);
        modelBuilder.Entity<SalesCreditNote>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<SalesCreditNote>().HasIndex(x => x.InvoiceId);
        modelBuilder.Entity<QcPlanLine>().HasOne(l => l.QcPlan).WithMany(p => p.Lines).HasForeignKey(l => l.QcPlanId);
        modelBuilder.Entity<QcInspectionLine>().HasOne(l => l.QcInspection).WithMany(p => p.Lines).HasForeignKey(l => l.QcInspectionId);

        // Chứng từ đã phát sinh không xóa dây chuyền
        foreach (var fk in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }
}
