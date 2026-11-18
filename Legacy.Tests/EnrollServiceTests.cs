using OnlineCourses;
using Xunit;

namespace Legacy.Tests;

public class EnrollServiceTests
{
    private readonly CourseRepository repo;
    private readonly EnrollService svc;

    public EnrollServiceTests()
    {
        CourseRepository.NextId = 1000;
        PromoCatalog.Used.Clear();
        PaymentService.TxCount = 0;
        repo = new CourseRepository();
        svc = new EnrollService(repo);
    }

    private void OpenAllYear(string code)
    {
        var c = repo.FindCourse(code);
        c.OpensOn = new DateTime(2026, 1, 1);
        c.ClosesOn = new DateTime(2026, 12, 31);
    }

    // ---------- Enroll: розрахунковий вузол ----------

    [Fact]
    public void Enroll_FreeLearnerWelcome10PayNow_ReturnsActive108()
    {
        var e = svc.Enroll(1, "CS101", "WELCOME10", true);
        Assert.NotNull(e);
        Assert.Equal(EnrollmentStatus.Active, e.Status);
        Assert.Equal(108m, e.PricePaid);
        Assert.Equal(1003, e.Id);
        Assert.Equal(192m, repo.FindLearner(1).Balance);
    }

    [Fact]
    public void Enroll_ProLearnerSpring20PayLater_ReturnsPending129_6()
    {
        var e = svc.Enroll(2, "CS201", "spring20", false);
        Assert.Equal(EnrollmentStatus.Pending, e.Status);
        Assert.Equal(129.6m, e.PricePaid);
        Assert.Equal("SPRING20", e.PromoCode);
        Assert.Equal(500m, repo.FindLearner(2).Balance);
    }

    [Fact]
    public void Enroll_CorpWithinGraceAfterClose_ReturnsActive127_5()
    {
        var e = svc.Enroll(4, "UX120", null, true);
        Assert.Equal(127.5m, e.PricePaid);
        Assert.Equal(127.5m, repo.FindLearner(4).CompanyDebt);
        Assert.Equal(0m, repo.FindLearner(4).Balance);
    }

    [Fact]
    public void Enroll_NewYearPromoInMarch_IgnoresPromo()
    {
        OpenAllYear("CS101");
        var e = svc.Enroll(1, "CS101", "NEWYEAR", false, new DateTime(2026, 3, 10));
        Assert.Equal(120m, e.PricePaid);
        Assert.Equal("", e.PromoCode);
    }

    [Fact]
    public void Enroll_NewYearPromoInJanuary_Gives25Percent()
    {
        OpenAllYear("CS101");
        var e = svc.Enroll(1, "CS101", "NEWYEAR", false, new DateTime(2026, 1, 15));
        Assert.Equal(90m, e.PricePaid);
        Assert.Equal("NEWYEAR", e.PromoCode);
    }

    // Характеризаційний тест. Вікно "закривається сьогодні", але код
    // відмовляє саме сьогодні (>=). Зафіксовано як наявна поведінка, див. TD-04.
    [Fact]
    public void Enroll_WindowClosesToday_ReturnsNull()
    {
        Assert.Null(svc.Enroll(1, "WEB110", "", true));
    }

    // Характеризаційний тест. Для CORP перед відкриттям вікна запис
    // дозволений (різниця днів від'ємна). Імовірна помилка, див. TD-04.
    [Fact]
    public void Enroll_CorpBeforeWindowOpens_ReturnsPending136()
    {
        var e = svc.Enroll(4, "PY150", "", false);
        Assert.Equal(EnrollmentStatus.Pending, e.Status);
        Assert.Equal(136m, e.PricePaid);
    }

    // ---------- Enroll: сценарії помилок (фіксують null) ----------

    [Fact]
    public void Enroll_PrerequisiteNotCompleted_ReturnsNull()
        => Assert.Null(svc.Enroll(3, "CS201", null, false));

    [Fact]
    public void Enroll_AlreadyEnrolled_ReturnsNullAndChargesOnce()
    {
        Assert.NotNull(svc.Enroll(1, "CS101", null, true));
        Assert.Null(svc.Enroll(1, " cs101 ", null, true));
        Assert.Equal(180m, repo.FindLearner(1).Balance);
    }

    [Fact]
    public void Enroll_UnknownCourse_ReturnsNull()
        => Assert.Null(svc.Enroll(1, "XX999", "", false));

    [Fact]
    public void Enroll_UnknownLearner_ReturnsNull()
        => Assert.Null(svc.Enroll(42, "CS101", null, false));

    [Fact]
    public void Enroll_NullCourseCode_ReturnsNull()
        => Assert.Null(svc.Enroll(1, null, null, false));

    [Fact]
    public void Enroll_ArchivedCourse_ReturnsNull()
    {
        repo.FindCourse("CS101").Archived = true;
        Assert.Null(svc.Enroll(1, "CS101", null, false));
    }

    [Fact]
    public void Enroll_NonCorpAfterClose_ReturnsNull()
        => Assert.Null(svc.Enroll(1, "UX120", null, false));

    [Fact]
    public void Enroll_NoSeats_ReturnsNullAndJoinsWaitlistOnce()
    {
        Assert.Null(svc.Enroll(5, "DS301", null, true));
        Assert.Null(svc.Enroll(5, "DS301", null, true));
        Assert.Equal(new List<int> { 5 }, repo.FindCourse("DS301").Waitlist);
    }

    // Характеризаційний тест. Id витрачається ще до списання коштів:
    // при нестачі коштів запис не додається, а NextId уже збільшено (TD-04).
    [Fact]
    public void Enroll_InsufficientBalance_ReturnsNullAndConsumesId()
    {
        Assert.Null(svc.Enroll(3, "CS101", null, true));
        Assert.Equal(3, repo.Enrollments.Count);
        Assert.Equal(1004, CourseRepository.NextId);
        Assert.Equal(40m, repo.FindLearner(3).Balance);
    }

    // ---------- Зміни стану ----------

    [Fact]
    public void Activate_PendingEnrollment_ChargesBalance()
    {
        var e = svc.Enroll(2, "CS201", "SPRING20", false);
        Assert.True(svc.Activate(e.Id));
        Assert.Equal(EnrollmentStatus.Active, e.Status);
        Assert.Equal(370.4m, repo.FindLearner(2).Balance);
    }

    [Fact]
    public void Activate_AlreadyActive_ReturnsFalse()
        => Assert.False(svc.Activate(1000));

    [Fact]
    public void Complete_ActiveEnrollment_StoresScoreAndProgress()
    {
        Assert.True(svc.Complete(1002, 92));
        var e = repo.Enrollments.Single(x => x.Id == 1002);
        Assert.Equal(EnrollmentStatus.Completed, e.Status);
        Assert.Equal(92, e.Score);
        Assert.Equal(100, e.Progress);
    }

    [Fact]
    public void Complete_DroppedEnrollment_ReturnsFalse()
    {
        Assert.True(svc.Drop(1001));
        Assert.False(svc.Complete(1001, 90));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(59)]
    [InlineData(101)]
    public void Complete_ScoreOutOfRange_ReturnsFalse(int score)
        => Assert.False(svc.Complete(1002, score));

    [Fact]
    public void Drop_ActiveWithin7DaysLowProgress_RefundsFull207()
    {
        Assert.True(svc.Drop(1001));
        Assert.Equal(207m, svc.LastRefund);
        Assert.Equal(407m, repo.FindLearner(6).Balance);
    }

    [Fact]
    public void Drop_FreesSeat_PromotesFirstFromWaitlist()
    {
        Assert.Null(svc.Enroll(5, "DS301", null, true));
        Assert.True(svc.Drop(1001));
        var p = repo.Enrollments.Single(x => x.Id == 1003);
        Assert.Equal(5, p.LearnerId);
        Assert.Equal(EnrollmentStatus.Pending, p.Status);
        Assert.Equal(230m, p.PricePaid);
        Assert.Empty(repo.FindCourse("DS301").Waitlist);
    }
}