using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;

namespace ERP.Web.Data;

public class ErpDbContext : DbContext
{
    public ErpDbContext(DbContextOptions<ErpDbContext> options) : base(options) { }

    // ------------------------------------------------------------------ Phạm vi chi nhánh & người thao tác (gán theo request)
    /// <summary>Bật lọc dữ liệu theo chi nhánh cho request hiện tại</summary>
    public bool BranchFilterOn { get; set; }
    public int[] ScopeBranchIds { get; set; } = Array.Empty<int>();
    /// <summary>Chi nhánh gán cho chứng từ mới tạo (chưa chọn chi nhánh)</summary>
    public int? DefaultBranchId { get; set; }
    public int? AuditUserId { get; set; }
    public string? AuditUserName { get; set; }
    public string? AuditIp { get; set; }
    /// <summary>Tạm tắt nhật ký (khi ghi chính bảng nhật ký)</summary>
    private bool _auditing;

    /// <summary>Chạy một đoạn với phạm vi chi nhánh khác (dùng cho báo cáo hợp nhất theo từng công ty)</summary>
    public async Task<T> WithScopeAsync<T>(int[]? branches, Func<Task<T>> action)
    {
        var (on, ids) = (BranchFilterOn, ScopeBranchIds);
        try { BranchFilterOn = branches != null; ScopeBranchIds = branches ?? Array.Empty<int>(); ChangeTracker.Clear(); return await action(); }
        finally { BranchFilterOn = on; ScopeBranchIds = ids; ChangeTracker.Clear(); }
    }

    private static readonly Type[] BranchFiltered =
    {
        typeof(JournalEntry), typeof(Invoice), typeof(PurchaseOrder), typeof(SalesOrder), typeof(InventoryDoc), typeof(CashVoucher), typeof(ProductionOrder),
        typeof(SalesCreditNote), typeof(FixedAsset), typeof(Contract), typeof(PayrollRun), typeof(BankStatement), typeof(DebtOffset), typeof(ToolItem),
        typeof(VatDeclaration), typeof(CitDeclaration), typeof(PurchaseRequisition), typeof(SalesQuote), typeof(Stocktake), typeof(LandedCost)
    };

    private void SetBranchFilter<T>(ModelBuilder mb) where T : class, IBranchOwned =>
        mb.Entity<T>().HasQueryFilter(e => !BranchFilterOn || e.BranchId == null || ScopeBranchIds.Contains(e.BranchId!.Value));

    private static int? _hqBranch;

    public override int SaveChanges(bool acceptAllChangesOnSuccess) => SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (_auditing) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        // Chi nhánh mặc định cho chứng từ mới
        var added = ChangeTracker.Entries<IBranchOwned>().Where(e => e.State == EntityState.Added && e.Entity.BranchId == null).ToList();
        if (added.Count > 0)
        {
            var branch = DefaultBranchId;
            if (branch == null)
            {
                try { _hqBranch ??= await Branches.IgnoreQueryFilters().OrderBy(b => b.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync(cancellationToken); }
                catch { /* bảng chưa có (đang khởi tạo) */ }
                branch = _hqBranch;
            }
            foreach (var e in added)
            {
                // Phiếu kho / phiếu thu chi lấy chi nhánh của kho / quỹ nếu có
                if (e.Entity is InventoryDoc d) e.Entity.BranchId = (await Warehouses.FindAsync(new object[] { d.WarehouseId }, cancellationToken))?.BranchId ?? branch;
                else if (e.Entity is CashVoucher v) e.Entity.BranchId = (await CashAccounts.FindAsync(new object[] { v.CashAccountId }, cancellationToken))?.BranchId ?? branch;
                else e.Entity.BranchId = branch;
            }
        }
        var pending = AuditUserId == null ? null : AuditTrail.Capture(this);
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (pending is { Count: > 0 })
        {
            _auditing = true;
            try { AuditLogs.AddRange(AuditTrail.Build(pending, AuditUserId, AuditUserName, AuditIp)); await base.SaveChangesAsync(true, cancellationToken); }
            catch { /* nhật ký không được làm hỏng nghiệp vụ */ }
            finally { _auditing = false; }
        }
        return result;
    }

    /// <summary>Ghi một dòng nhật ký nghiệp vụ (duyệt, đăng nhập...)</summary>
    public async Task LogAsync(string action, string entityType, string entityName, object? entityId, string? docNumber, string? changes = null)
    {
        _auditing = true;
        try
        {
            AuditLogs.Add(new AuditLog { Action = action, EntityType = entityType, EntityName = entityName, EntityId = entityId?.ToString(), DocNumber = docNumber, Changes = changes, UserId = AuditUserId, UserName = AuditUserName, Ip = AuditIp });
            await base.SaveChangesAsync(true, default);
        }
        catch { }
        finally { _auditing = false; }
    }

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
    public DbSet<SavedReport> SavedReports => Set<SavedReport>();
    public DbSet<SalesCreditNote> SalesCreditNotes => Set<SalesCreditNote>();
    public DbSet<SalesCreditLine> SalesCreditLines => Set<SalesCreditLine>();
    public DbSet<SalesCreditOffset> SalesCreditOffsets => Set<SalesCreditOffset>();
    // Nền tảng: đa công ty, nhật ký, phê duyệt, cài đặt
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ApprovalRule> ApprovalRules => Set<ApprovalRule>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<CodeRule> CodeRules => Set<CodeRule>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<AccountMapping> AccountMappings => Set<AccountMapping>();
    // Nhân sự, lương
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollLine> PayrollLines => Set<PayrollLine>();
    // Tiền tệ, hợp đồng, công nợ, ngân hàng
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<DebtOffset> DebtOffsets => Set<DebtOffset>();
    public DbSet<DebtOffsetLine> DebtOffsetLines => Set<DebtOffsetLine>();
    public DbSet<BankStatement> BankStatements => Set<BankStatement>();
    public DbSet<BankStatementLine> BankStatementLines => Set<BankStatementLine>();
    // Tài sản, phân bổ, ngân sách, thuế
    public DbSet<AssetTransfer> AssetTransfers => Set<AssetTransfer>();
    public DbSet<ToolItem> ToolItems => Set<ToolItem>();
    public DbSet<ToolAllocationRun> ToolAllocationRuns => Set<ToolAllocationRun>();
    public DbSet<RecurringJournal> RecurringJournals => Set<RecurringJournal>();
    public DbSet<RecurringJournalLine> RecurringJournalLines => Set<RecurringJournalLine>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<BudgetLine> BudgetLines => Set<BudgetLine>();
    public DbSet<VatDeclaration> VatDeclarations => Set<VatDeclaration>();
    public DbSet<CitDeclaration> CitDeclarations => Set<CitDeclaration>();
    public DbSet<TaxPortalInvoice> TaxPortalInvoices => Set<TaxPortalInvoice>();
    public DbSet<ScaleTicket> ScaleTickets => Set<ScaleTicket>();
    // Mua hàng, bán hàng, kho nâng cao
    public DbSet<PurchaseRequisition> PurchaseRequisitions => Set<PurchaseRequisition>();
    public DbSet<PurchaseRequisitionLine> PurchaseRequisitionLines => Set<PurchaseRequisitionLine>();
    public DbSet<Rfq> Rfqs => Set<Rfq>();
    public DbSet<RfqLine> RfqLines => Set<RfqLine>();
    public DbSet<RfqVendor> RfqVendors => Set<RfqVendor>();
    public DbSet<RfqQuote> RfqQuotes => Set<RfqQuote>();
    public DbSet<SupplierEvaluation> SupplierEvaluations => Set<SupplierEvaluation>();
    public DbSet<LandedCost> LandedCosts => Set<LandedCost>();
    public DbSet<LandedCostLine> LandedCostLines => Set<LandedCostLine>();
    public DbSet<ContractMilestone> ContractMilestones => Set<ContractMilestone>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<PriceListLine> PriceListLines => Set<PriceListLine>();
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<SalesQuote> SalesQuotes => Set<SalesQuote>();
    public DbSet<SalesQuoteLine> SalesQuoteLines => Set<SalesQuoteLine>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<SalesTarget> SalesTargets => Set<SalesTarget>();
    public DbSet<CommissionTier> CommissionTiers => Set<CommissionTier>();
    public DbSet<ItemGroup> ItemGroups => Set<ItemGroup>();
    public DbSet<UomConversion> UomConversions => Set<UomConversion>();
    public DbSet<StockLot> StockLots => Set<StockLot>();
    public DbSet<StockLotMove> StockLotMoves => Set<StockLotMove>();
    public DbSet<BinLocation> BinLocations => Set<BinLocation>();
    public DbSet<StockBin> StockBins => Set<StockBin>();
    public DbSet<Stocktake> Stocktakes => Set<Stocktake>();
    public DbSet<StocktakeLine> StocktakeLines => Set<StocktakeLine>();
    public DbSet<Pallet> Pallets => Set<Pallet>();
    public DbSet<PalletItem> PalletItems => Set<PalletItem>();

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
        modelBuilder.Entity<SavedReport>().HasIndex(x => new { x.OwnerId, x.Name });
        modelBuilder.Entity<QcPlanLine>().HasOne(l => l.QcPlan).WithMany(p => p.Lines).HasForeignKey(l => l.QcPlanId);
        modelBuilder.Entity<QcInspectionLine>().HasOne(l => l.QcInspection).WithMany(p => p.Lines).HasForeignKey(l => l.QcInspectionId);
        modelBuilder.Entity<Company>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Branch>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<AuditLog>().HasIndex(x => x.At);
        modelBuilder.Entity<AuditLog>().HasIndex(x => new { x.EntityType, x.EntityId });
        modelBuilder.Entity<ApprovalRequest>().HasIndex(x => new { x.DocType, x.DocId });
        modelBuilder.Entity<CodeRule>().HasIndex(x => x.Target).IsUnique();
        modelBuilder.Entity<ApiClient>().HasIndex(x => x.KeyPrefix).IsUnique();
        modelBuilder.Entity<Employee>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Attendance>().HasIndex(x => new { x.EmployeeId, x.Year, x.Month }).IsUnique();
        modelBuilder.Entity<PayrollRun>().HasIndex(x => new { x.Year, x.Month, x.BranchId });
        modelBuilder.Entity<Currency>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<ExchangeRate>().HasIndex(x => new { x.CurrencyCode, x.Date }).IsUnique();
        modelBuilder.Entity<Contract>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<DebtOffset>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<ToolItem>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<ToolAllocationRun>().HasIndex(x => new { x.Year, x.Month }).IsUnique();
        modelBuilder.Entity<AccountMapping>().HasIndex(x => new { x.Standard, x.FromPrefix }).IsUnique();
        modelBuilder.Entity<ExchangeRate>().Property(x => x.Rate).HasPrecision(18, 4);
        modelBuilder.Entity<Invoice>().Property(x => x.ExchangeRate).HasPrecision(18, 4);
        modelBuilder.Entity<CashVoucher>().Property(x => x.ExchangeRate).HasPrecision(18, 4);
        modelBuilder.Entity<InvoiceLine>().Property(x => x.FcUnitPrice).HasPrecision(18, 4);
        modelBuilder.Entity<PurchaseRequisition>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Rfq>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<LandedCost>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<SalesQuote>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<PriceList>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<ItemGroup>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<StockLot>().HasIndex(x => new { x.ItemId, x.WarehouseId, x.LotNo }).IsUnique();
        modelBuilder.Entity<StockLotMove>().HasIndex(x => new { x.ItemId, x.LotNo });
        modelBuilder.Entity<BinLocation>().HasIndex(x => new { x.WarehouseId, x.Code }).IsUnique();
        modelBuilder.Entity<StockBin>().HasIndex(x => new { x.ItemId, x.WarehouseId, x.BinId }).IsUnique();
        modelBuilder.Entity<Stocktake>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Pallet>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<SalesTarget>().HasIndex(x => new { x.UserId, x.Year, x.Month }).IsUnique();
        modelBuilder.Entity<UomConversion>().HasIndex(x => new { x.ItemId, x.Unit }).IsUnique();
        modelBuilder.Entity<UomConversion>().Property(x => x.Factor).HasPrecision(18, 6);
        modelBuilder.Entity<RecurringJournalLine>().Property(x => x.Percent).HasPrecision(9, 4);
        modelBuilder.Entity<ApprovalStep>().HasOne(s => s.ApprovalRequest).WithMany(r => r.Steps).HasForeignKey(s => s.ApprovalRequestId);
        modelBuilder.Entity<AppUser>().HasOne(u => u.Branch).WithMany().HasForeignKey(u => u.BranchId);

        // Phạm vi dữ liệu theo chi nhánh (chỉ áp dụng khi người dùng bị giới hạn chi nhánh hoặc đang chọn xem một chi nhánh)
        foreach (var t in BranchFiltered)
            typeof(ErpDbContext).GetMethod(nameof(SetBranchFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(t).Invoke(this, new object[] { modelBuilder });
        modelBuilder.Entity<JournalLine>().HasQueryFilter(l => !BranchFilterOn || l.JournalEntry!.BranchId == null || ScopeBranchIds.Contains(l.JournalEntry.BranchId.Value));

        // Chứng từ đã phát sinh không xóa dây chuyền
        foreach (var fk in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }
}
