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
        catch { }    // TODO: повторити пізніше
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
        if (!course.PrerequisitesMetBy(learner))
            throw new EnrollmentException("prerequisites not completed");
        System.Diagnostics.Debug.Assert(course.Seats > 0, "Seats must be positive");
        if (CountTaken(course) >= course.Seats)
        {
            if (!course.Waitlist.Contains(learner.Id))
            {
                course.Waitlist.Add(learner.Id);
                log.Write("WAITLIST " + learner.Id + " " + course.Code);
            }
            throw new EnrollmentException("no free seats in " + course.Code);
        }
        decimal price = calc.BasePrice(course, learner);
        if (promo != null)
        {
            promo = promo.Trim().ToUpper();
            // тимчасово, поки нема дат дії: NEWYEAR - січень
            if (promo == "NEWYEAR" && today.Month != NewYearMonth)
                promo = "";
            int pct = PromoCatalog.GetPercent(promo);
            price = price - price * pct / PercentBase;
        }
        Enrollment e = CreatePending(learner, course, price, today);
        e.PromoCode = promo;
        if (payNow)
        {
            try
            {
                payments.Charge(learner, price);
            }
            catch (Exception ex)
            {
                throw new EnrollmentException("payment failed: " + ex.Message);
            }
            e.Status = EnrollmentStatus.Active;
            e.PaidOn = today;
        }
        repo.Enrollments.Add(e);
        log.Write("ENROLL " + e.Id + " " + learner.Id + " " + course.Code + " " + price);
        return e;
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

    private static void EnsureWindowIsOpen(Course course, Learner learner, DateTime today)
    {
        if (today < course.OpensOn || today >= course.ClosesOn)
        {
            if (learner.Plan != "CORP" || (today - course.ClosesOn).Days > CorpGraceDays)
            {
                Console.WriteLine("   enrollment window is closed for " + course.Code);
                throw new EnrollmentException("enrollment window is closed for " + course.Code);
            }
        }
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
}
