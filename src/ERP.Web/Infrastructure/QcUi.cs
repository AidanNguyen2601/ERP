using ERP.Web.Models.Entities;

namespace ERP.Web.Infrastructure;

/// <summary>Màu nhãn trạng thái cho màn hình quản lý chất lượng</summary>
public static class QcUi
{
    public static string Badge(QcResult r) => r switch
    {
        QcResult.Pass => "bg-success",
        QcResult.Conditional => "bg-warning text-dark",
        QcResult.Fail => "bg-danger",
        _ => "bg-secondary"
    };
    public static string Badge(QcStatus s) => s switch
    {
        QcStatus.Completed => "bg-primary",
        QcStatus.Cancelled => "bg-secondary",
        _ => "bg-light text-dark border"
    };
    public static string Badge(NcrStatus s) => s == NcrStatus.Open ? "bg-warning text-dark" : "bg-success";
    public static string Badge(NcrDisposition d) => d switch
    {
        NcrDisposition.Scrap => "bg-danger",
        NcrDisposition.ReturnToVendor => "bg-info text-dark",
        NcrDisposition.Rework => "bg-primary",
        NcrDisposition.AcceptAsIs => "bg-success",
        _ => "bg-light text-dark border"
    };
    public static string Icon(QcStage s) => s switch
    {
        QcStage.Incoming => "bi-box-arrow-in-down",
        QcStage.InProcess => "bi-gear",
        QcStage.Final => "bi-award",
        _ => "bi-truck"
    };
}
