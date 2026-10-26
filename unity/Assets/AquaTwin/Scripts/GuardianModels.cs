using System;

namespace AquaTwin
{
    [Serializable] public sealed class GuardianPoint { public double day, depth_cm, lower_cm, upper_cm, tds_ppm; }
    [Serializable] public sealed class GuardianBacktest { public double mae_cm, median_absolute_error_cm, interval_coverage; public int held_out_samples; }
    [Serializable] public sealed class GuardianInsight
    {
        public int schema_version, sample_count, aggregate_count;
        public string well_id, source, model_version, model, quality, risk, message, vertical_datum, aquifer_id;
        public double computed_at, valid_until, window_hours, source_start, source_end, depth_cm, column_cm, tds_ppm;
        public double drawdown_cm_day, tds_change_ppm_day, depth_change_cm, anomaly_score, max_horizon_days;
        public double time_to_critical_days, time_to_tds_review_days, reference_elevation_m;
        public bool tds_calibrated;
        public GuardianPoint[] forecasts, history;
        public string[] alerts, flags, neighbors;
        public GuardianBacktest backtest;
        [NonSerialized] public bool HasCriticalCrossing, HasTdsCrossing, HasDatum, Example;
        public bool Ready(double now) => (quality == "EXPLORATORY" || quality == "ILLUSTRATIVE") && (Example || valid_until >= now);
        public GuardianPoint At(double day)
        {
            if (forecasts == null || forecasts.Length == 0) return null;
            day = Math.Max(0, Math.Min(max_horizon_days, day));
            var a = forecasts[0];
            foreach (var b in forecasts)
            {
                if (b.day < day) { a = b; continue; }
                double f = b.day == a.day ? 0 : (day-a.day)/(b.day-a.day);
                return new GuardianPoint { day=day, depth_cm=a.depth_cm+(b.depth_cm-a.depth_cm)*f,
                    lower_cm=a.lower_cm+(b.lower_cm-a.lower_cm)*f, upper_cm=a.upper_cm+(b.upper_cm-a.upper_cm)*f,
                    tds_ppm=a.tds_ppm+(b.tds_ppm-a.tds_ppm)*f };
            }
            return a;
        }
    }
    [Serializable] public sealed class GuardianScenarioInputs
    {
        public double flow_m3_hour=2, hours_day=6, reduction_percent=30, days=7, start_day=0, recharge_m3_day=0;
        public double area_m2=10000, specific_yield=.15;
    }
    [Serializable] public class GuardianScenario
    {
        public string request_id, well_id, source="hypothetical", message;
        public double computed_at, baseline_m3_day, scenario_m3_day, avoided_m3, days, start_day, baseline_total_m3, scenario_total_m3;
        public double baseline_depth_cm, scenario_depth_cm, scenario_lower_cm, scenario_upper_cm;
        public bool level_model_available;
        public GuardianScenarioInputs inputs;
    }
    public static class GuardianScenarioMath
    {
        public static GuardianScenario Calculate(GuardianScenarioInputs x, double depth, bool headModel)
        {
            bool Valid(double n,double lo,double hi) => !double.IsNaN(n)&&!double.IsInfinity(n)&&n>=lo&&n<=hi;
            if (!Valid(x.flow_m3_hour,0,100000)||!Valid(x.hours_day,0,24)||!Valid(x.reduction_percent,0,100)||
                !Valid(x.days,.01,365)||!Valid(x.start_day,0,x.days)||!Valid(x.recharge_m3_day,0,1e8)) throw new ArgumentException("Check flow, hours, reduction and days.");
            if (headModel && (!Valid(x.area_m2,.01,1e12)||!Valid(x.specific_yield,.0001,1)||!Valid(depth,0,1e6)))
                throw new ArgumentException("The hypothetical response needs positive area, specific yield and a reference reading.");
            double baseline=x.flow_m3_hour*x.hours_day, scenario=baseline*(1-x.reduction_percent/100);
            var result=new GuardianScenario { baseline_m3_day=baseline,scenario_m3_day=scenario,
                avoided_m3=(baseline-scenario)*(x.days-x.start_day),days=x.days,start_day=x.start_day,
                baseline_total_m3=baseline*x.days,scenario_total_m3=baseline*x.start_day+scenario*(x.days-x.start_day),inputs=x,
                level_model_available=headModel,message="Hypothetical inputs. Avoided abstraction is not proven recharge or aquifer recovery." };
            if (headModel)
            {
                double change=(result.scenario_total_m3-x.recharge_m3_day*x.days)/(x.area_m2*x.specific_yield)*100;
                result.baseline_depth_cm=depth+(baseline-x.recharge_m3_day)*x.days/(x.area_m2*x.specific_yield)*100;
                result.scenario_depth_cm=depth+change;
                result.scenario_lower_cm=Math.Min(depth+change/.8,depth+change/1.2);
                result.scenario_upper_cm=Math.Max(depth+change/.8,depth+change/1.2);
                result.message="Hypothetical uniform unconfined balance; sensitivity band varies area +/-20%. Not calibrated groundwater recovery.";
            }
            return result;
        }
    }
}
