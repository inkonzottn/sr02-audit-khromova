namespace OnlineCourses;

// Запис на курси, оплата, відмова, завершення.
// v2.3 - waitlist (Oleh), v2.4 - corporate plan (Iryna S.)
public class EnrollService
{
    private CourseRepository repo;
    private PaymentService payments = new PaymentService();
    private PriceCalculator calc = new PriceCalculator();
    private AuditLog log = new AuditLog();
    public decimal LastRefund;       // для екрана підтвердження
    private const int CorpGraceDays = 3;
    private const int PercentBase = 100;
    private const int NewYearMonth = 1;
    private const int MinPassScore = 60;
    private const int MaxScore = 100;

    public EnrollService(CourseRepository r)
    {
        repo = r;
    }

    // підтвердження оплати для Pending
    public bool Activate(int enrollmentId)
    {
        Enrollment e = repo.FindEnrollment(enrollmentId);
        if (e == null)
            return false;
        Learner l = repo.FindLearner(e.LearnerId);
        if (e.Status != EnrollmentStatus.Pending)
            return false;
        try { payments.Charge(l, e.PricePaid); }
        catch { return false; }
        e.PaidOn = DateTime.Today;
        log.Write("ACTIVATE " + e.Id);
        return e.ChangeStatus(EnrollmentStatus.Active);
    }

    // відмова від курсу, гроші повертаються на баланс
    public bool Drop(int enrollmentId)
    {
        Enrollment e = repo.FindEnrollment(enrollmentId);
        if (e == null)
            return false;
        Learner l = repo.FindLearner(e.LearnerId);
        decimal r = calc.Refund(e);
        if (!e.ChangeStatus(EnrollmentStatus.Dropped))
            return false;
        LastRefund = r;
        try { if (r > 0) payments.Refund(l, r); }
        catch { }    // TODO: TD-05 повторити пізніше
        log.Write("DROP " + e.Id + " refund " + r);
        PromoteFromWaitlist(repo.FindCourse(e.CourseCode));
        return true;
    }

    public bool Complete(int enrollmentId, int score)
    {
        Enrollment e = repo.FindEnrollment(enrollmentId);
        if (e == null)
            return false;
        Learner l = repo.FindLearner(e.LearnerId);
        if (score < MinPassScore  || score > MaxScore)
            return false;
        if (!e.ChangeStatus(EnrollmentStatus.Completed))
            return false;
        e.Score = score;
        e.Progress = PercentBase;
        if (!l.CompletedCourses.Contains(e.CourseCode))
            l.CompletedCourses.Add(e.CourseCode);
        log.Write("COMPLETE " + e.Id + " score " + score);
        return true;
    }

    // TODO: надсилати лист замість виводу в консоль
    public void PromoteFromWaitlist(Course c)
    {
        // одне місце звільнилось - беремо першого з черги
        if (c == null || c.Waitlist.Count == 0)
            return;
        Learner l = repo.FindLearner(c.Waitlist[0]);
        c.Waitlist.RemoveAt(0);
        decimal price = calc.BasePrice(c, l);
        DateTime today = DateTime.Today;
        Enrollment e = CreatePending(l, c, price, today);
        repo.Enrollments.Add(e);
        Console.WriteLine("   [waitlist] " + l.Email + " moved to " + c.Code + " as #" + e.Id);
    }

    private int CountTaken(Course c)
    {
        int taken = 0;
        foreach (Enrollment x in repo.Enrollments)
            if (x.CourseCode == c.Code && x.Status != EnrollmentStatus.Dropped)
                taken++;
        return taken;
    }

    // TODO: TD-04
    private Enrollment CreatePending(Learner learner, Course course, decimal price, DateTime today)
    {
        Enrollment e = new Enrollment();
        e.Id = CourseRepository.NextId++;
        e.LearnerId = learner.Id;
        e.CourseCode = course.Code;
        e.PricePaid = price;
        e.EnrolledOn = today;
        e.Status = EnrollmentStatus.Pending;
        return e;
    }

        public Enrollment Enroll(int learnerId, string courseCode, string promo, bool payNow,
        DateTime? now = null)
    {
        try
        {
            return EnrollOrThrow(learnerId, courseCode, promo, payNow, now ?? DateTime.Today);
        }
        catch (EnrollmentException)
        {
            return null;
        }
    }

    private Enrollment EnrollOrThrow(int learnerId, string courseCode, string promo,
        bool payNow, DateTime today)
    {
        Learner learner = RequireLearner(learnerId);
        Course course = RequireCourse(courseCode);
        EnsureWindowIsOpen(course, learner, today);
        EnsureNotEnrolled(course, learner);
        EnsurePrerequisites(course, learner);
        EnsureSeatOrWaitlist(course, learner);
        string appliedPromo = NormalizePromo(promo, today);
        decimal price = ApplyPromo(calc.BasePrice(course, learner), appliedPromo);
        Enrollment e = CreatePending(learner, course, price, today);
        e.PromoCode = appliedPromo;
        if (payNow)
            ChargeOrThrow(e, learner, today);

        repo.Enrollments.Add(e);
        log.Write("ENROLL " + e.Id + " " + learner.Id + " " + course.Code + " " + price);
        return e;
    }

    private void ChargeOrThrow(Enrollment e, Learner learner, DateTime today)
    {
        try
        {
            payments.Charge(learner, e.PricePaid);
        }
        catch (Exception ex)
        {
            throw new EnrollmentException("payment failed: " + ex.Message);
        }
        e.Status = EnrollmentStatus.Active;
        e.PaidOn = today;
    }

    private decimal ApplyPromo(decimal basePrice, string appliedPromo)
    {
        if (string.IsNullOrEmpty(appliedPromo))
            return basePrice;

        int pct = PromoCatalog.GetPercent(appliedPromo);
        return basePrice - basePrice * pct / PercentBase;
    }

    private string NormalizePromo(string promo, DateTime today)
    {
        promo = promo.Trim().ToUpper();
        bool outOfSeason = promo == "NEWYEAR" && today.Month != NewYearMonth;
        if (outOfSeason)
            promo = "";
        return promo;
    }

    private void EnsureSeatOrWaitlist(Course course, Learner learner)
    {
        if (CountTaken(course) >= course.Seats)
        {
            if (!course.Waitlist.Contains(learner.Id))
            {
                course.Waitlist.Add(learner.Id);
                log.Write("WAITLIST " + learner.Id + " " + course.Code);
            }
            throw new EnrollmentException("no free seats in " + course.Code);
        }
    }

    private static void EnsurePrerequisites(Course course, Learner learner)
    {
        if (!course.PrerequisitesMetBy(learner))
            throw new EnrollmentException("prerequisites not completed");
        System.Diagnostics.Debug.Assert(course.Seats > 0, "Seats must be positive");
    }

    private void EnsureNotEnrolled(Course course, Learner learner)
    {
        foreach (Enrollment x in repo.Enrollments)
        {
            if (x.CourseCode == course.Code && x.Status != EnrollmentStatus.Dropped
                && x.LearnerId == learner.Id)
            {
                Console.WriteLine("   " + learner.Email + " is already enrolled in " + course.Code);
                throw new EnrollmentException("already enrolled");
            }
        }
    }

    private void EnsureWindowIsOpen(Course c, Learner l, DateTime today)
    {
        if (!IsOutsideWindow(c, today))
            return;
        if (HasCorporateGrace(c, l, today))
            return;
        Console.WriteLine("   enrollment window is closed for " + c.Code); // TODO: TD-07
        throw new EnrollmentException("enrollment window is closed for " + c.Code);
    }

    private Course RequireCourse(string courseCode)
    {
        Course course = repo.FindCourse(courseCode);
        if (courseCode == null)
            throw new EnrollmentException("course code is required");
        return course ?? throw new EnrollmentException("course " + courseCode + " not found");
    }

    private Learner RequireLearner(int learnerId)
    {
        Learner l = repo.FindLearner(learnerId);
        if (l == null)
            throw new EnrollmentException("learner " + learnerId + " not found");
        return l;
    }

    // TODO: TD-04
    private static bool IsOutsideWindow(Course c, DateTime today)
    => today < c.OpensOn || today >= c.ClosesOn;

    // TODO: TD-04
    private static bool HasCorporateGrace(Course c, Learner l, DateTime today)
        => l.Plan == "CORP" && (today - c.ClosesOn).Days <= CorpGraceDays;
}
