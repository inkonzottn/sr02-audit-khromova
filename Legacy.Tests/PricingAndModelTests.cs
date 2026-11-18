using OnlineCourses;
using Xunit;

namespace Legacy.Tests;

public class PriceCalculatorTests
{
    private static Enrollment Make(EnrollmentStatus s, int daysAgo, int progress,
        string promo = null) => new Enrollment
    {
        Status = s, PricePaid = 200m, Progress = progress, PromoCode = promo,
        EnrolledOn = DateTime.Today.AddDays(-daysAgo)
    };

    [Theory]
    [InlineData(0, 0, 200)]
    [InlineData(7, 30, 200)]
    [InlineData(8, 0, 100)]
    [InlineData(14, 30, 100)]
    [InlineData(15, 0, 0)]
    [InlineData(3, 31, 0)]
    public void Refund_ActiveByDaysAndProgress_ReturnsCapturedValue(
        int days, int progress, double expected)
    {
        var actual = new PriceCalculator().Refund(Make(EnrollmentStatus.Active, days, progress));
        Assert.Equal((decimal)expected, actual);
    }

    [Fact]
    public void Refund_Pending_ReturnsZero()
        => Assert.Equal(0m, new PriceCalculator().Refund(Make(EnrollmentStatus.Pending, 1, 0)));

    [Fact]
    public void Refund_Staff50Promo_ReturnsZero()
        => Assert.Equal(0m, new PriceCalculator().Refund(Make(EnrollmentStatus.Active, 1, 0, "STAFF50")));

    [Theory]
    [InlineData("FREE", 6, 120, 120)]
    [InlineData("PRO", 10, 250, 207)]
    [InlineData("STUDENT", 6, 120, 60)]
    [InlineData("CORP", 5, 150, 127.5)]
    public void BasePrice_PlanAndWeeks_ReturnsCapturedValue(
        string plan, int weeks, double price, double expected)
    {
        var c = new Course { Price = (decimal)price, Weeks = weeks };
        var l = new Learner { Plan = plan };
        Assert.Equal((decimal)expected, new PriceCalculator().BasePrice(c, l));
    }
}

public class EnrollmentStatusTests
{
    [Fact]
    public void ChangeStatus_PendingToActive_ReturnsTrue()
        => Assert.True(new Enrollment { Status = EnrollmentStatus.Pending }
            .ChangeStatus(EnrollmentStatus.Active));

    [Fact]
    public void ChangeStatus_PendingToCompleted_ReturnsFalse()
        => Assert.False(new Enrollment { Status = EnrollmentStatus.Pending }
            .ChangeStatus(EnrollmentStatus.Completed));

    [Fact]
    public void ChangeStatus_DroppedToActive_ReturnsFalse()
        => Assert.False(new Enrollment { Status = EnrollmentStatus.Dropped }
            .ChangeStatus(EnrollmentStatus.Active));
}