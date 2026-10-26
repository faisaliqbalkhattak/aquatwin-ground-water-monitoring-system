using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

namespace AquaTwin.Editor
{
    public static class GuardianValidation
    {
#if UNITY_EDITOR
        [MenuItem("AquaTwin/Validate Guardian",priority=35)]
#endif
        public static void Run()
        {
            int checks=0;
            void Check(bool condition,string why){if(!condition)throw new Exception("Guardian: "+why);checks++;}
            var x=new GuardianScenarioInputs();
            var result=GuardianScenarioMath.Calculate(x,280,false);
            Check(Math.Abs(result.baseline_m3_day-12)<1e-9,"baseline units");
            Check(Math.Abs(result.scenario_m3_day-8.4)<1e-9&&Math.Abs(result.avoided_m3-25.2)<1e-9,"report's 30% pumping example");
            Check(!result.level_model_available,"no aquifer-recovery claim without explicit assumptions");
            x.reduction_percent=0;Check(GuardianScenarioMath.Calculate(x,280,false).avoided_m3==0,"zero reduction conserves volume");
            x.reduction_percent=30;x.start_day=2;result=GuardianScenarioMath.Calculate(x,280,true);
            Check(Math.Abs(result.avoided_m3-18)<1e-9&&result.scenario_depth_cm<result.baseline_depth_cm,"intervention date and hypothetical response");
            x.specific_yield=0;bool rejected=false;try{GuardianScenarioMath.Calculate(x,280,true);}catch(ArgumentException){rejected=true;}
            Check(rejected,"invalid physical assumptions rejected");
            var insight=new GuardianInsight {quality="EXPLORATORY",valid_until=100,max_horizon_days=30,
                forecasts=new[]{new GuardianPoint{day=0,depth_cm=280,lower_cm=279,upper_cm=281,tds_ppm=450},new GuardianPoint{day=30,depth_cm=340,lower_cm=338,upper_cm=342,tds_ppm=570}}};
            Check(insight.Ready(99)&&!insight.Ready(101),"expired insight cannot drive a forecast");
            Check(Math.Abs(insight.At(20).depth_cm-320)<1e-9&&Math.Abs(insight.At(20).tds_ppm-530)<1e-9,"timeline uses correct units");
            Check(insight.At(100).day==30,"horizon remains evidence bounded");
            insight.quality="INSUFFICIENT_EVIDENCE";Check(!insight.Ready(50),"short evidence cannot drive a forecast");
            var sent=new List<byte[]>();
            var mqtt=new MqttProtocolSession("u","p",new[]{"aquatwin/well/+/telemetry","aquatwin/insights/well/+"},0,new[]{"aquatwin/insights/well/+"}){Send=bytes=>sent.Add(bytes)};
            mqtt.Open(0);mqtt.Feed(new byte[]{0x20,2,0,0,0x90,4,0,1,0,128},1);
            Check(mqtt.State=="CONNECTED"&&mqtt.SubscriptionWarnings.Contains("Guardian"),"optional insights denial preserves raw MQTT functionality");
            Check(mqtt.Publish("aquatwin/guardian/requests/test","{}")&&sent[sent.Count-1][0]==0x30,"scenario requests are non-retained publishes");
            Check(!mqtt.Publish("invalid/+","{}"),"request topics cannot contain wildcards");
#if UNITY_EDITOR
            Debug.Log("AQUATWIN_GUARDIAN_CHECKS_PASSED: "+checks);
#else
            Console.WriteLine("AQUATWIN_GUARDIAN_CHECKS_PASSED: "+checks);
#endif
        }
    }
}
