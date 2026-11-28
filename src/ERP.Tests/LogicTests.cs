using ERP.Web.Models.Entities;
using ERP.Web.Services;
using Xunit;

namespace ERP.Tests;

/// <summary>Kiểm thử logic thuần (không cần database): công thức lương, thuế TNCN, tính lương, phép năm, lịch báo cáo, cơ hội CRM</summary>
public class FormulaTests
{
    private static readonly Dictionary<string, decimal> V = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BaseSalary"] = 10_000_000, ["WorkDays"] = 26, ["StdDays"] = 26, ["Years"] = 7, ["Dependents"] = 2
    };

    [Theory]
    [InlineData("1+2*3", 7)]
    [InlineData("(1+2)*3", 9)]
    [InlineData("10/4", 2.5)]
    [InlineData("-5+2", -3)]
    [InlineData("BaseSalary*0.01", 100000)]
    [InlineData("MIN(Years;10)*100000", 700000)]
    [InlineData("MIN(Years;5)*100000", 500000)]
    [InlineData("MAX(1;2;3)", 3)]
    [InlineData("ROUND(10/3)", 3)]
    [InlineData("IF(WorkDays >= StdDays; 500000; 0)", 500000)]
    [InlineData("IF(WorkDays < StdDays; 500000; 0)", 0)]
    [InlineData("Dependents*200000+Years", 400007)]
    public void Eval_ReturnsExpected(string expr, double expected) =>
        Assert.Equal((decimal)expected, FormulaEvaluator.Eval(expr, V));

    [Theory]
    [InlineData("BaseSalary/(")]
    [InlineData("1+")]
    [InlineData("Unknown*2")]
    [InlineData("FOO(1)")]
    [InlineData("(1+2")]
    public void Validate_RejectsBadFormula(string expr) => Assert.NotNull(FormulaEvaluator.Validate(expr));

    [Fact]
    public void Validate_AcceptsGoodFormula() => Assert.Null(FormulaEvaluator.Validate("IF(WorkDays >= StdDays; MIN(Years;10)*100000; 0)"));

    [Fact]
    public void Eval_DivisionByZero_Throws() => Assert.ThrowsAny<Exception>(() => FormulaEvaluator.Eval("1/0", V));
}

public class PayrollTests
{
    private static readonly List<(decimal Upper, decimal Rate)> Brackets = new()
    {
        (5_000_000, 5), (10_000_000, 10), (18_000_000, 15), (32_000_000, 20), (52_000_000, 25), (80_000_000, 30), (0, 35)
    };

    private static PayrollService.Rates Rates() => new(8, 1.5m, 1, 17.5m, 3, 1, 2, 11_000_000, 4_400_000, 46_800_000, 26, Brackets);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-100, 0)]
    [InlineData(5_000_000, 250_000)]
    [InlineData(10_000_000, 750_000)]
    [InlineData(18_000_000, 1_950_000)]
    [InlineData(32_000_000, 4_750_000)]
    [InlineData(100_000_000, 100_000_000 * 0 + 4_750_000 + 5_000_000 * 0 + 20_000_000 * 0.25 + 28_000_000 * 0.30 + 20_000_000 * 0.35)]
    public void Pit_IsProgressive(double taxable, double expected) =>
        Assert.Equal((decimal)expected, PayrollService.Pit((decimal)taxable, Brackets));

    private static Employee Emp(decimal baseSalary = 20_000_000, int dependents = 0) => new()
    {
        Id = 1, FullName = "Test", BaseSalary = baseSalary, InsuranceSalary = baseSalary, Dependents = dependents, Department = "KD", ExpenseAccount = "642"
    };

    [Fact]
    public void Compute_NetEqualsGrossMinusInsuranceTaxDeductions()
    {
        var l = new PayrollLine();
        PayrollService.Compute(l, Emp(), 26, 0, 26, Rates());
        Assert.Equal(20_000_000, l.Gross);
        Assert.Equal(l.Gross - l.EmpTotal - l.Pit - l.OtherDeduction, l.Net);
        Assert.Equal(Math.Round(20_000_000m * 8 / 100), l.EmpBhxh);
        Assert.True(l.Pit > 0);
    }

    [Fact]
    public void Compute_InsuranceCapApplies()
    {
        var l = new PayrollLine();
        PayrollService.Compute(l, Emp(100_000_000), 26, 0, 26, Rates());
        Assert.Equal(46_800_000, l.InsuranceBase);
    }

    [Fact]
    public void Compute_NoTaxBelowDeduction()
    {
        var l = new PayrollLine();
        PayrollService.Compute(l, Emp(10_000_000, 2), 26, 0, 26, Rates());
        Assert.Equal(0, l.Pit);
        Assert.Equal(0, l.TaxableIncome);
    }

    [Fact]
    public void Compute_PartialMonthProratesSalary()
    {
        var l = new PayrollLine();
        PayrollService.Compute(l, Emp(26_000_000), 13, 0, 26, Rates());
        Assert.Equal(13_000_000, l.SalaryByDays);
    }

    [Fact]
    public void Compute_ExtrasAffectGrossTaxableAndNet()
    {
        var plain = new PayrollLine(); var withX = new PayrollLine();
        PayrollService.Compute(plain, Emp(), 26, 0, 26, Rates());
        PayrollService.Compute(withX, Emp(), 26, 0, 26, Rates(), new HrService.Extras(2_000_000, 500_000, 1_000_000, 0, 300_000, "x"));
        Assert.Equal(plain.Gross + 2_500_000, withX.Gross);
        Assert.True(withX.TaxableIncome >= plain.TaxableIncome + 2_000_000 - 1);
        Assert.Equal(withX.Gross - withX.EmpTotal - withX.Pit - 300_000, withX.Net);
    }

    [Fact]
    public void Compute_OvertimeTaxExemptPortion()
    {
        var l = new PayrollLine();
        PayrollService.Compute(l, Emp(26_000_000), 26, 10, 26, Rates());
        Assert.Equal(Math.Round(26_000_000m / 26 / 8 * 1.5m * 10), l.Overtime);
    }
}

public class HrTests
{
    [Fact]
    public void Entitlement_BaseAndTenureBonus()
    {
        var e = new Employee { StartDate = new DateTime(2015, 3, 1), AnnualLeaveDays = 12 };
        Assert.Equal(13, HrService.Entitlement(e, 2020));    // đủ 5 năm → +1
        Assert.Equal(14, HrService.Entitlement(e, 2025));    // 10 năm → +2
        Assert.Equal(12, HrService.Entitlement(e, 2018));
    }

    [Fact]
    public void Entitlement_ProratedInStartYear()
    {
        var e = new Employee { StartDate = new DateTime(2026, 7, 1), AnnualLeaveDays = 12 };
        Assert.Equal(6, HrService.Entitlement(e, 2026));
    }

    [Fact]
    public void LaborContract_DaysLeft()
    {
        var c = new LaborContract { Status = LaborContractStatus.Active, EndDate = DateTime.Today.AddDays(10) };
        Assert.Equal(10, c.DaysLeft);
        c.Status = LaborContractStatus.Terminated;
        Assert.Null(c.DaysLeft);
    }
}

public class BiAndCrmTests
{
    [Fact]
    public void Schedule_Daily_NextIsTodayOrTomorrow()
    {
        var s = new ReportSchedule { Frequency = ScheduleFrequency.Daily, Hour = 7 };
        Assert.Equal(new DateTime(2026, 11, 28, 7, 0, 0), s.NextAfter(new DateTime(2026, 11, 28, 6, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 29, 7, 0, 0), s.NextAfter(new DateTime(2026, 11, 28, 8, 0, 0)));
    }

    [Fact]
    public void Schedule_Weekly_NextIsMonday()
    {
        var s = new ReportSchedule { Frequency = ScheduleFrequency.Weekly, Hour = 7 };
        var n = s.NextAfter(new DateTime(2026, 11, 28, 9, 0, 0));   // thứ Bảy
        Assert.Equal(DayOfWeek.Monday, n.DayOfWeek);
        Assert.Equal(new DateTime(2026, 11, 30, 7, 0, 0), n);
    }

    [Fact]
    public void Schedule_Monthly_NextIsFirstOfMonth()
    {
        var s = new ReportSchedule { Frequency = ScheduleFrequency.Monthly, Hour = 6 };
        Assert.Equal(new DateTime(2026, 12, 1, 6, 0, 0), s.NextAfter(new DateTime(2026, 11, 28, 9, 0, 0)));
        Assert.Equal(new DateTime(2027, 1, 1, 6, 0, 0), s.NextAfter(new DateTime(2026, 12, 1, 9, 0, 0)));
    }

    [Theory]
    [InlineData(OppStage.Prospecting, 10)]
    [InlineData(OppStage.Proposal, 50)]
    [InlineData(OppStage.Won, 100)]
    [InlineData(OppStage.Lost, 0)]
    public void Opportunity_DefaultProbability(OppStage s, int p) => Assert.Equal(p, Opportunity.DefaultProbability(s));

    [Fact]
    public void Opportunity_WeightedAndOpen()
    {
        var o = new Opportunity { Amount = 1_000_000, Probability = 25, Stage = OppStage.Proposal };
        Assert.Equal(250_000, o.Weighted);
        Assert.True(o.IsOpen);
        o.Stage = OppStage.Won;
        Assert.False(o.IsOpen);
    }

    [Fact]
    public void Task_OverdueOnlyWhenOpen()
    {
        var a = new CrmActivity { Kind = ActivityKind.Task, Done = false, Date = DateTime.Now.AddDays(-1) };
        Assert.True(a.IsOverdue);
        a.Done = true;
        Assert.False(a.IsOverdue);
    }
}
