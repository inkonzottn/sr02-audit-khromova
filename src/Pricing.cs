namespace OnlineCourses;

// Промокоди - від маркетингу, міняти тільки через них.
public static class PromoCatalog
{
    public static Dictionary<string, int> Used = new Dictionary<string, int>();

    // TODO TD-01: CC = 9, потрібен Replace Conditional with Table;
    public static int GetPercent(string code)
    {
        int pct = 0;
        switch (code)
        {
            case "WELCOME10": pct = 10; break;
            case "SPRING20": pct = 20; break;
            case "NEWYEAR": pct = 25; break;
            case "STAFF50":
                // не більше 3 співробітників на потік
                if (!Used.ContainsKey(code) || Used[code] < 3)
                    pct = 50;
                break;
        }
        if (pct > 0)
        {
            if (Used.ContainsKey(code))
                Used[code] = Used[code] + 1;
            else
                Used[code] = 1;
        }
        return pct;
    }
}

public class PriceCalculator
{
    private const int LongCourseWeeks = 8;
    private const decimal LongCourseDiscount = 20m;
    private const decimal ProFactor = 0.9m;
    private const decimal CorpFactor = 0.85m;
    private const decimal StudentFactor = 0.5m;
    private const int FullRefundDays = 7;
    private const int HalfRefundDays = 14;
    private const int MaxProgressForRefund = 30;
    private const int HalfRefundDivisor = 2;

    private static readonly Dictionary<string, decimal> PlanFactors =
    new Dictionary<string, decimal>
{
    { "PRO", ProFactor }, { "CORP", CorpFactor }, { "STUDENT", StudentFactor }
};

    public decimal BasePrice(Course c, Learner l)
    {
        decimal p = c.Price;
        if (c.Weeks > LongCourseWeeks)
            p = p - LongCourseDiscount;
        if (PlanFactors.TryGetValue(l.Plan, out decimal factor))
            p = p * factor;
        return p;
    }

    // Повернення при відмові від курсу:
    // повна сума протягом 14 днів, далі - нічого.
    public decimal Refund(Enrollment e)
    {
        if (e.Status == EnrollmentStatus.Pending || e.Status == EnrollmentStatus.Completed)
            return 0;
        if (e.PromoCode == "STAFF50")
            return 0;
        int d = (DateTime.Today - e.EnrolledOn).Days;
        decimal r = 0;
        if (d <= FullRefundDays)
            r = e.PricePaid;
        else if (d <= HalfRefundDays)
            r = e.PricePaid / HalfRefundDivisor;
        // more than 30% of lessons watched - no refund
        if (e.Progress > MaxProgressForRefund)
            r = 0;
        return Math.Round(r, 2);
    }

    // стара формула (до 2023), лишили для звірки
    public decimal OldPrice(Course c, int weeks)
    {
        return weeks > 6 ? c.Price * weeks / 6 : c.Price;
    }

}
