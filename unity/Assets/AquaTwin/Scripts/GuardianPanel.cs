using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace AquaTwin
{
    /// <summary>Progressive disclosure keeps investigation tools out of the monitoring canvas.</summary>
    public sealed class GuardianPanel
    {
        private readonly VisualElement overlay, body;
        private readonly GuardianFeed feed;
        private readonly FleetScene scene;
        private readonly Func<WellFleet.Well> selected;
        private readonly Func<bool> demo;
        private readonly Label title, badge, status;
        private readonly List<Button> tabs=new();
        private string page="Evidence", wellId;
        private Label overview, forecast, scenarioResult, requests;
        private ForecastChart chart;
        private Slider horizon;
        private DoubleField flow,hours,reduction,days,start,recharge,area,yield;
        private Toggle model;
        private VisualElement assumptions, saved;
        private float nextUpdate;
        private int savedCount;
        public bool Opened => overlay.style.display==DisplayStyle.Flex;
        public GuardianPanel(VisualElement root,GuardianFeed feed,FleetScene scene,Func<WellFleet.Well> selected,Func<bool> demo)
        {
            this.feed=feed;feed.PrepareLocalHistory();this.scene=scene;this.selected=selected;this.demo=demo;
            overlay=Box(root,"guardian-overlay");overlay.style.display=DisplayStyle.None;
            var card=Box(overlay,"guardian-card");var head=Box(card,"guardian-header");
            var heading=Box(head,"guardian-heading");Text(heading,"GUARDIAN · DETECT / PREDICT / INTERVENE","eyebrow");
            title=Text(heading,"Well intelligence","guardian-title");
            Button(head,"×",Close);badge=Text(card,"","guardian-badge");
            var row=Box(card,"guardian-tabs");foreach(var name in new[]{"Evidence","Time machine","What-if","Abu Dhabi context"})
            {var tabName=name;var b=Button(row,name,()=>Show(tabName));tabs.Add(b);}
            var scroll=new ScrollView(ScrollViewMode.Vertical);scroll.AddToClassList("guardian-scroll");card.Add(scroll);body=scroll;
            status=Text(card,"","guardian-service");
            var footer=Box(card,"guardian-footer");Text(footer,"Measured, calculated, forecast and hypothetical results remain separate.","muted");
            Button(footer,"View twin",()=>overlay.style.display=DisplayStyle.None);Button(footer,"Close",Close);overlay.RegisterCallback<KeyDownEvent>(e=>{if(e.keyCode==KeyCode.Escape){Close();e.StopPropagation();}});
        }
        public void Open(){overlay.style.display=DisplayStyle.Flex;wellId=selected()?.Id;Show(page);overlay.Focus();}
        public void Close(){overlay.style.display=DisplayStyle.None;scene.ClearProjection();}
        private void Show(string name)
        {
            page=name;body.Clear();foreach(var tab in tabs)tab.EnableInClassList("active",tab.text==name);
            overview=forecast=scenarioResult=requests=null;chart=null;horizon=null;
            if(name=="Evidence")overview=Text(body,"","guardian-evidence");
            else if(name=="Time machine")
            {
                Text(body,"A trend baseline, with its evidence and uncertainty","section-title");
                horizon=new Slider("Days ahead",0,30){value=7};horizon.AddToClassList("guardian-input");body.Add(horizon);
                horizon.RegisterValueChangedCallback(_=>Refresh());
                forecast=Text(body,"","guardian-evidence");chart=new ForecastChart();chart.AddToClassList("guardian-chart");body.Add(chart);
                Text(body,"Solid line: stored history. Dashed line: trend extrapolation. Shaded area: heuristic residual band, not calibrated confidence.","muted");
                Text(body,"The translucent orange column is the projected baseline; the teal column remains the received reading.","muted");
            }
            else if(name=="What-if")BuildScenario();
            else
            {
                Text(body,"Historical Abu Dhabi groundwater context","section-title");
                Text(body,"Environment Agency – Abu Dhabi: salinity (2017 / 2008), groundwater-level change (2005–2017), 2017 contours and depleted-area context. These are historical datasets, not current sensor measurements.","guardian-evidence");
                Button(body,"Open historical map",()=>Application.OpenURL(ContextUrl()));
                Button(body,"View EAD source catalogue",()=>Application.OpenURL("https://edp.ead.ae/server/rest/services/Enviroportal/Groundwater/MapServer"));
                Text(body,"The 3D twin uses normalized locations and relative well-column fill. Without reference elevations, a common datum and known aquifer connectivity, its envelope remains schematic. Estimated TDS is not a confirmed contamination plume.","guardian-evidence");
            }
            nextUpdate=0;Refresh();
        }
        private void BuildScenario()
        {
            Text(body,"Test an abstraction change","section-title");
            Text(body,"Inputs are user-entered assumptions. This calculation never changes sensor readings or operates a pump.","muted");
            var row=Box(body,"guardian-field-row");flow=Field(row,"Flow · m³/hour",2);hours=Field(row,"Runtime · hours/day",6);
            row=Box(body,"guardian-field-row");reduction=Field(row,"Reduction · %",30);days=Field(row,"Horizon · days",7);
            row=Box(body,"guardian-field-row");start=Field(row,"Start after · days",0);recharge=Field(row,"Assumed recharge · m³/day",0);
            model=new Toggle("Add hypothetical level-response model");model.AddToClassList("guardian-input");body.Add(model);
            assumptions=Box(body,"guardian-field-row");area=Field(assumptions,"Effective area · m²",10000);yield=Field(assumptions,"Specific yield · fraction",.15);
            assumptions.style.display=DisplayStyle.None;model.RegisterValueChangedCallback(e=>assumptions.style.display=e.newValue?DisplayStyle.Flex:DisplayStyle.None);
            Button(body,"Calculate & save scenario",()=>{
                try
                {
                    var well=selected();if(well==null)throw new ArgumentException("Select a well first.");
                    feed.Calculate(well.Id,new GuardianScenarioInputs {flow_m3_hour=flow.value,hours_day=hours.value,reduction_percent=reduction.value,days=days.value,
                        start_day=start.value,recharge_m3_day=recharge.value,area_m2=area.value,specific_yield=yield.value},model.value,well.DepthCm,demo());
                    RefreshSaved();Refresh();
                }catch(Exception e){scenarioResult.text=e.Message;}
            });
            scenarioResult=Text(body,"","guardian-evidence");requests=Text(body,"","muted");
            Text(body,"Saved comparisons","section-title");
            Button(body,"Load saved scenarios from service",()=>{if(!demo())feed.LoadSaved(selected()?.Id);});
            saved=Box(body,"guardian-saved");RefreshSaved();
        }
        private void RefreshSaved()
        {
            if(saved==null)return;saved.Clear();savedCount=feed.Scenarios.Count;int count=0;
            foreach(var item in feed.Scenarios)
            {
                if(item.well_id!=selected()?.Id||++count>10)continue;
                var scenario=item;Button(saved,$"{F(item.days)} days · {F(item.avoided_m3)} m³ avoided",()=>{feed.SelectedScenario=scenario;Refresh();});
            }
        }
        public void Tick()
        {
            if(!Opened||Time.realtimeSinceStartup<nextUpdate)return;
            nextUpdate=Time.realtimeSinceStartup+1;
            if(wellId!=selected()?.Id){wellId=selected()?.Id;Show(page);}else Refresh();
        }
        private void Refresh()
        {
            var well=selected();var value=feed.Find(well?.Id,demo());
            title.text=well==null?"Select a well":well.Id;
            badge.text=demo()?"ILLUSTRATIVE · SEPARATE DESIGNED 30-DAY HISTORY":value==null?"NO ANALYSIS RECEIVED":$"{value.source?.ToUpperInvariant()} · {value.quality?.Replace('_',' ')}";
            status.text=demo()?"Analytical example is independent of the 90-second animation.":feed.ServiceState;
            bool ready=value!=null&&value.Ready(GuardianFeed.UtcNow);
            if(overview!=null)
            {
                overview.text=value==null?"Run the Guardian worker to accumulate durable history and receive evidence-based insights. Current telemetry and the local demo continue to work independently.":
                    value.message+"\n\n"+ (ready?$"Drawdown  {F(value.drawdown_cm_day)} cm/day\nTDS change  {F(value.tds_change_ppm_day)} ppm/day\nDepth change  {F(value.depth_change_cm)} cm\n\n":"")+
                    $"Evidence: {value.sample_count} observations · {F(value.window_hours)} hours\nModel: {value.model_version} · {value.model}\n"+
                    Join(value.alerts)+Join(value.flags)+Join(value.neighbors)+
                    (value.backtest==null?"":$"\nHeld-out MAE {F(value.backtest.mae_cm)} cm · median error {F(value.backtest.median_absolute_error_cm)} cm · band coverage {F(value.backtest.interval_coverage*100)}% ({value.backtest.held_out_samples} samples)");
                if(value!=null&&!ready&&value.valid_until<GuardianFeed.UtcNow&&!demo())overview.text="LAST KNOWN ANALYSIS · forecast disabled until the service refreshes.\n\n"+overview.text;
            }
            if(forecast!=null)
            {
                horizon.SetEnabled(ready);horizon.highValue=ready?(float)value.max_horizon_days:30;
                if(!ready){forecast.text=value?.message??"Waiting for enough persisted history.";chart.Value=null;scene.ClearProjection();}
                else
                {
                    var point=value.At(horizon.value);
                    forecast.text=$"Day {F(point.day)} · depth {F(point.depth_cm)} cm\nHeuristic band {F(point.lower_cm)}–{F(point.upper_cm)} cm · TDS {F(point.tds_ppm)} ppm\n"+
                        (value.HasCriticalCrossing?$"Operating-depth crossing: {F(value.time_to_critical_days)} days\n":"Operating-depth crossing: not established within supported horizon\n")+
                        (value.HasTdsCrossing?$"TDS review crossing: {F(value.time_to_tds_review_days)} days":"TDS review crossing: not established within supported horizon");
                    chart.Value=value;scene.SetProjection(well.Id,point,null,value.Example?double.MaxValue:value.valid_until);
                }
                chart.MarkDirtyRepaint();
            }
            if(scenarioResult!=null)
            {
                var result=feed.SelectedScenario;
                if(result!=null&&result.well_id==well?.Id)
                {
                    scenarioResult.text=$"Baseline  {F(result.baseline_m3_day)} m³/day\nIntervention  {F(result.scenario_m3_day)} m³/day\nAvoided abstraction  {F(result.avoided_m3)} m³ over {F(result.days)} days\n\n"+result.message;
                    if(result.level_model_available)
                    {
                        scenarioResult.text+=$"\nBaseline depth {F(result.baseline_depth_cm)} cm · scenario {F(result.scenario_depth_cm)} cm\nSensitivity {F(result.scenario_lower_cm)}–{F(result.scenario_upper_cm)} cm";
                        scene.SetProjection(well.Id,new GuardianPoint {depth_cm=result.baseline_depth_cm},result);
                    }else scene.ClearProjection();
                }
                requests.text=feed.RequestState;
                if(savedCount!=feed.Scenarios.Count)RefreshSaved();
            }
            if(page!="Time machine"&&page!="What-if")scene.ClearProjection();
        }
        private static string ContextUrl()
        {
            if(!string.IsNullOrEmpty(Application.absoluteURL))return new Uri(new Uri(Application.absoluteURL),"guardian-context/index.html").AbsoluteUri;
#if UNITY_EDITOR
            return new Uri(System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../../guardian/web/guardian-context/index.html"))).AbsoluteUri;
#else
            return "https://edp.ead.ae/server/rest/services/Enviroportal/Groundwater/MapServer";
#endif
        }
        private static string Join(string[] values)=>values==null||values.Length==0?"":"\n• "+string.Join("\n• ",values)+"\n";
        private static string F(double value)=>value.ToString("0.##",CultureInfo.InvariantCulture);
        private static VisualElement Box(VisualElement parent,string css){var e=new VisualElement();e.AddToClassList(css);parent.Add(e);return e;}
        private static Label Text(VisualElement parent,string value,string css){var e=new Label(value){enableRichText=false};e.AddToClassList(css);parent.Add(e);return e;}
        private static Button Button(VisualElement parent,string value,Action action){var e=new Button(action){text=value};e.AddToClassList("tool");parent.Add(e);return e;}
        private static DoubleField Field(VisualElement parent,string name,double value){var e=new DoubleField(name){value=value};e.AddToClassList("guardian-input");parent.Add(e);return e;}
        private sealed class ForecastChart:VisualElement
        {
            public GuardianInsight Value;
            public ForecastChart(){generateVisualContent+=Draw;}
            private void Draw(MeshGenerationContext c)
            {
                if(Value?.forecasts==null||Value.forecasts.Length<2)return;
                var all=new List<GuardianPoint>();if(Value.history!=null)all.AddRange(Value.history);all.AddRange(Value.forecasts);
                double lo=double.PositiveInfinity,hi=double.NegativeInfinity,minDay=0,maxDay=Value.max_horizon_days;
                foreach(var p in all){lo=Math.Min(lo,p.lower_cm==0?p.depth_cm:p.lower_cm);hi=Math.Max(hi,p.upper_cm==0?p.depth_cm:p.upper_cm);minDay=Math.Min(minDay,p.day);}
                float width=contentRect.width,height=contentRect.height;var painter=c.painter2D;
                Vector2 XY(double day,double depth)=>new Vector2(12+(float)((day-minDay)/Math.Max(1,maxDay-minDay))*(width-24),12+(float)((depth-lo)/Math.Max(1,hi-lo))*(height-24));
                painter.fillColor=new Color(.9f,.65f,.26f,.12f);painter.BeginPath();
                for(int i=0;i<Value.forecasts.Length;i++){var p=Value.forecasts[i];if(i==0)painter.MoveTo(XY(p.day,p.lower_cm));else painter.LineTo(XY(p.day,p.lower_cm));}
                for(int i=Value.forecasts.Length-1;i>=0;i--){var p=Value.forecasts[i];painter.LineTo(XY(p.day,p.upper_cm));}painter.ClosePath();painter.Fill();
                painter.lineWidth=2;painter.strokeColor=new Color(.36f,.86f,.73f);
                if(Value.history?.Length>1){painter.BeginPath();for(int i=0;i<Value.history.Length;i++){var p=Value.history[i];if(i==0)painter.MoveTo(XY(p.day,p.depth_cm));else painter.LineTo(XY(p.day,p.depth_cm));}painter.Stroke();}
                painter.strokeColor=new Color(.98f,.71f,.35f);
                for(int i=1;i<Value.forecasts.Length;i++){var a=Value.forecasts[i-1];var b=Value.forecasts[i];for(int j=0;j<12;j+=2){painter.BeginPath();painter.MoveTo(Vector2.Lerp(XY(a.day,a.depth_cm),XY(b.day,b.depth_cm),j/12f));painter.LineTo(Vector2.Lerp(XY(a.day,a.depth_cm),XY(b.day,b.depth_cm),(j+1)/12f));painter.Stroke();}}
            }
        }
    }
}
