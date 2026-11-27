using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ERP.Web.Models.Entities;

namespace ERP.Web.Data;

/// <summary>Ghi vết thêm / sửa / xóa các chứng từ và danh mục quan trọng (người, thời điểm, giá trị cũ → mới)</summary>
public static class AuditTrail
{
    public record Pending(EntityEntry Entry, string Action, string Type, string Name, Dictionary<string, object?[]>? Changes);

    /// <summary>Kiểu được ghi vết → tên hiển thị</summary>
    private static readonly Dictionary<Type, string> Tracked = new()
    {
        [typeof(PurchaseRequisition)] = "Đề nghị mua", [typeof(Rfq)] = "Yêu cầu báo giá", [typeof(SupplierEvaluation)] = "Đánh giá NCC", [typeof(LandedCost)] = "Chi phí mua hàng", [typeof(PriceList)] = "Bảng giá", [typeof(Promotion)] = "Khuyến mại",
        [typeof(SalesQuote)] = "Báo giá bán", [typeof(Shipment)] = "Vận chuyển", [typeof(Stocktake)] = "Đợt kiểm kê", [typeof(ItemGroup)] = "Nhóm hàng", [typeof(Pallet)] = "Pallet", [typeof(BinLocation)] = "Vị trí kho", [typeof(CommissionTier)] = "Bậc hoa hồng", [typeof(ContractMilestone)] = "Đợt hợp đồng",
        [typeof(AppUser)] = "Người dùng", [typeof(Partner)] = "Đối tác", [typeof(Item)] = "Hàng hóa", [typeof(Account)] = "Tài khoản", [typeof(Warehouse)] = "Kho",
        [typeof(Employee)] = "Nhân viên", [typeof(Company)] = "Công ty", [typeof(Branch)] = "Chi nhánh", [typeof(CashAccount)] = "Quỹ / ngân hàng",
        [typeof(PurchaseOrder)] = "Đơn mua", [typeof(SalesOrder)] = "Đơn bán", [typeof(Invoice)] = "Hóa đơn", [typeof(CashVoucher)] = "Phiếu thu / chi",
        [typeof(InventoryDoc)] = "Phiếu kho", [typeof(JournalEntry)] = "Bút toán", [typeof(FixedAsset)] = "Tài sản cố định", [typeof(ToolItem)] = "Công cụ dụng cụ",
        [typeof(ProductionOrder)] = "Lệnh sản xuất", [typeof(SalesCreditNote)] = "HĐ điều chỉnh giảm", [typeof(QcNonconformance)] = "NCR",
        [typeof(PostingRule)] = "Cấu hình bút toán", [typeof(ApprovalRule)] = "Quy tắc duyệt", [typeof(Budget)] = "Ngân sách", [typeof(Contract)] = "Hợp đồng",
        [typeof(PayrollRun)] = "Bảng lương", [typeof(FiscalPeriod)] = "Kỳ kế toán", [typeof(CodeRule)] = "Quy tắc đặt mã", [typeof(ApiClient)] = "Ứng dụng tích hợp",
        [typeof(DebtOffset)] = "Bù trừ công nợ", [typeof(Currency)] = "Tiền tệ", [typeof(ExchangeRate)] = "Tỷ giá", [typeof(RecurringJournal)] = "Bút toán định kỳ",
        [typeof(VatDeclaration)] = "Tờ khai GTGT", [typeof(CitDeclaration)] = "Tờ khai TNDN", [typeof(SystemSetting)] = "Cài đặt hệ thống",
    };

    private static readonly HashSet<string> Secret = new(StringComparer.OrdinalIgnoreCase) { "PasswordHash", "KeyHash", "EInvoiceXml" };

    public static string? NameOf(Type t) => Tracked.GetValueOrDefault(t);

    public static List<Pending> Capture(DbContext db)
    {
        var list = new List<Pending>();
        foreach (var e in db.ChangeTracker.Entries())
        {
            if (!Tracked.TryGetValue(e.Entity.GetType(), out var name)) continue;
            switch (e.State)
            {
                case EntityState.Added: list.Add(new Pending(e, "Tạo", e.Entity.GetType().Name, name, null)); break;
                case EntityState.Deleted: list.Add(new Pending(e, "Xóa", e.Entity.GetType().Name, name, Snapshot(e))); break;
                case EntityState.Modified:
                    var ch = new Dictionary<string, object?[]>();
                    foreach (var p in e.Properties.Where(p => p.IsModified && !Secret.Contains(p.Metadata.Name)))
                        if (!Equals(p.OriginalValue, p.CurrentValue)) ch[p.Metadata.Name] = new[] { Plain(p.OriginalValue), Plain(p.CurrentValue) };
                    if (e.Properties.Any(p => p.IsModified && p.Metadata.Name == "PasswordHash")) ch["Mật khẩu"] = new object?[] { "***", "*** (đổi mới)" };
                    if (e.Entity is SystemSetting ss && (ss.Key.Contains("Key", StringComparison.OrdinalIgnoreCase) || ss.Key.Contains("Password", StringComparison.OrdinalIgnoreCase)) && ch.ContainsKey("Value"))
                        ch["Value"] = new object?[] { "***", "*** (đổi mới)" };
                    if (ch.Count == 0) continue;
                    var action = ch.ContainsKey("Status") ? "Đổi trạng thái" : "Sửa";
                    list.Add(new Pending(e, action, e.Entity.GetType().Name, name, ch));
                    break;
            }
        }
        return list;
    }

    private static Dictionary<string, object?[]> Snapshot(EntityEntry e) =>
        e.Properties.Where(p => !Secret.Contains(p.Metadata.Name)).ToDictionary(p => p.Metadata.Name, p => new[] { Plain(p.OriginalValue), (object?)null });

    private static object? Plain(object? v) => v switch { null => null, DateTime d => d.ToString("yyyy-MM-dd HH:mm"), Enum en => en.ToString(), decimal m => m, bool b => b, int i => i, _ => v.ToString() };

    private static string? DocNo(object entity)
    {
        var t = entity.GetType();
        foreach (var n in new[] { "Number", "Code", "Username", "Name", "Key" })
            if (t.GetProperty(n)?.GetValue(entity) is string s && s.Length > 0) return s.Length > 100 ? s[..100] : s;
        return null;
    }

    public static IEnumerable<AuditLog> Build(List<Pending> pending, int? userId, string? userName, string? ip)
    {
        foreach (var p in pending)
        {
            var key = p.Entry.Metadata.FindPrimaryKey()?.Properties.Select(k => p.Entry.Property(k.Name).CurrentValue ?? p.Entry.Property(k.Name).OriginalValue).FirstOrDefault();
            string? changes = null;
            if (p.Changes != null)
            {
                var json = JsonSerializer.Serialize(p.Changes);
                changes = json.Length > 8000 ? json[..8000] : json;
            }
            yield return new AuditLog
            {
                At = DateTime.Now, UserId = userId, UserName = userName, Ip = ip, Action = p.Action, EntityType = p.Type, EntityName = p.Name,
                EntityId = key?.ToString(), DocNumber = DocNo(p.Entry.Entity), Changes = changes
            };
        }
    }
}
