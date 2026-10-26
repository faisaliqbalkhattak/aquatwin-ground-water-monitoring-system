using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace AquaTwin
{
    [DisallowMultipleComponent]
    public sealed class GuardianFeed : MonoBehaviour
    {
        private static string clientId;
        public static string ClientId => clientId ??= "unity_"+Guid.NewGuid().ToString("N").Substring(0,16);
        public static string RequestTopic => "aquatwin/guardian/requests/"+ClientId;
        public static string[] OptionalTopics => new[] {"aquatwin/insights/well/+", "aquatwin/guardian/replies/"+ClientId, "aquatwin/guardian/status"};
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] private static void ResetClientId() { clientId=null; }
        public static GuardianFeed Ensure(GameObject host) => host.GetComponent<GuardianFeed>() ?? host.AddComponent<GuardianFeed>();
        private readonly Dictionary<string,GuardianInsight> insights=new();
        private readonly Dictionary<string,GuardianInsight> examples=new();
        public readonly List<GuardianScenario> Scenarios=new();
        public GuardianScenario SelectedScenario;
        private string serviceState="Waiting for Guardian service";
        private double lastServiceAt;
        public string ServiceState { get => lastServiceAt>0&&UtcNow-lastServiceAt>90?"Guardian service has not refreshed recently":serviceState; private set=>serviceState=value; }
        public string RequestState="";
        private string pending;
        private float deadline;
        private bool loadedExamples;
        private string localKey;
        public static double UtcNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0;
        public static bool IsGuardianTopic(string topic) => topic.StartsWith("aquatwin/insights/")||topic.StartsWith("aquatwin/guardian/");
        public void PrepareLocalHistory()
        {
            var settings=AquaTwinMqttLocalSettings.Load();
            string key="AquaTwin.Guardian.Scenarios.v1."+Hash128.Compute(settings.Endpoint.AbsoluteUri+"|"+settings.username);
            if(localKey==key)return;localKey=key;Scenarios.Clear();SelectedScenario=null;insights.Clear();lastServiceAt=0;ServiceState="Waiting for Guardian service";
            try
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                string encoded=PlayerPrefs.GetString(localKey,"");
#else
                string path=Path.Combine(Path.GetDirectoryName(AquaTwinMqttLocalSettings.FilePath),localKey+".json");
                string encoded=File.Exists(path)?File.ReadAllText(path):"";
#endif
                if(!string.IsNullOrEmpty(encoded))Scenarios.AddRange(JsonUtility.FromJson<ScenarioList>(encoded).items??Array.Empty<GuardianScenario>());
            }catch{RequestState="Previous local scenarios could not be loaded.";}
        }
        private void SaveLocalHistory()
        {
            try
            {
                if(Scenarios.Count>30)Scenarios.RemoveRange(30,Scenarios.Count-30);
                string encoded=JsonUtility.ToJson(new ScenarioList{items=Scenarios.ToArray()});
                if(System.Text.Encoding.UTF8.GetByteCount(encoded)>131072)throw new IOException("Scenario storage budget exceeded");
#if UNITY_WEBGL && !UNITY_EDITOR
                PlayerPrefs.SetString(localKey,encoded);PlayerPrefs.Save();
#else
                AquaTwinSettingsFileStore.WriteAtomic(Path.Combine(Path.GetDirectoryName(AquaTwinMqttLocalSettings.FilePath),localKey+".json"),encoded);
#endif
            }catch{RequestState="Scenario calculated, but local storage is unavailable.";}
        }
        public GuardianInsight Find(string wellId, bool demo=false)
        {
            if (wellId==null)return null;
            if (demo)
            {
                if (!loadedExamples)
                {
                    loadedExamples=true;
                    var asset=Resources.Load<TextAsset>("GuardianExamples");
                    if(asset!=null)
                    {
                        var fields=WellFleet.ReadJsonObject(asset.text);
                        var records=(List<object>)fields["insights"];
                        foreach(var value in JsonUtility.FromJson<ExampleList>(asset.text).insights)
                        {
                            var record=records.Find(x=>((Dictionary<string,object>)x)["well_id"] as string==value.well_id) as Dictionary<string,object>;
                            ApplyPresence(value,record);value.Example=true; examples[value.well_id]=value;
                        }
                    }
                }
                return examples.TryGetValue(wellId,out var sample)?sample:null;
            }
            return insights.TryGetValue(wellId,out var result)?result:null;
        }
        public void Receive(string topic,string json)
        {
            try
            {
                if(json.Length>65536)throw new FormatException("Guardian payload too large");
                if(topic=="aquatwin/guardian/status")
                { var status=JsonUtility.FromJson<ServiceStatus>(json);lastServiceAt=status.computed_at;ServiceState=status.state=="ONLINE"&&UtcNow-status.computed_at<90?"Guardian connected":"Guardian last known / offline";return; }
                if(topic.StartsWith("aquatwin/insights/well/"))
                {
                    var value=JsonUtility.FromJson<GuardianInsight>(json);
                    if(value.schema_version!=1||topic!="aquatwin/insights/well/"+value.well_id||!Finite(value.computed_at)||value.computed_at>UtcNow+300||!Finite(value.valid_until))
                        throw new FormatException("Invalid Guardian insight contract");
                    var fields=WellFleet.ReadJsonObject(json);
                    ApplyPresence(value,fields);
                    if(value.max_horizon_days<0||value.max_horizon_days>365||!Finite(value.max_horizon_days)||
                        (value.forecasts?.Length??0)>256)throw new FormatException("Invalid Guardian forecast range");
                    double previousDay=-1;
                    foreach(var point in value.forecasts??Array.Empty<GuardianPoint>())
                    {
                        if(!Finite(point.day)||!Finite(point.depth_cm)||!Finite(point.lower_cm)||!Finite(point.upper_cm)||!Finite(point.tds_ppm)||point.day<previousDay)
                            throw new FormatException("Invalid Guardian forecast point");
                        previousDay=point.day;
                    }
                    if((value.quality=="EXPLORATORY"||value.quality=="ILLUSTRATIVE")&&(value.forecasts==null||value.forecasts.Length==0))
                        throw new FormatException("Forecast quality has no forecast points");
                    if(insights.TryGetValue(value.well_id,out var previous)&&previous.computed_at>value.computed_at)return;
                    insights[value.well_id]=value;lastServiceAt=value.computed_at;ServiceState="Guardian insights received";return;
                }
                if(topic=="aquatwin/guardian/replies/"+ClientId)
                {
                    WellFleet.ReadJsonObject(json); // strict finite-number / JSON validation before native deserialization
                    var response=JsonUtility.FromJson<Reply>(json);
                    if(response.request_id!=pending)return;
                    pending=null;
                    if(!response.ok){RequestState="Service: "+response.error;return;}
                    if(response.action=="scenario")
                    { SelectedScenario=response.result;Scenarios.Insert(0,response.result);RequestState="Scenario saved by Guardian service";SaveLocalHistory(); }
                    else if(response.result?.scenarios!=null)
                    { foreach(var item in response.result.scenarios)if(!Scenarios.Exists(x=>x.request_id==item.request_id))Scenarios.Add(item);RequestState="Saved scenarios loaded";SaveLocalHistory(); }
                }
            }
            catch(Exception e){ServiceState="Guardian message rejected: "+e.Message;}
        }
        public void Calculate(string wellId,GuardianScenarioInputs inputs,bool headModel,double depth,bool demo)
        {
            PrepareLocalHistory();
            SelectedScenario=GuardianScenarioMath.Calculate(inputs,depth,headModel);
            SelectedScenario.well_id=wellId;SelectedScenario.computed_at=UtcNow;SelectedScenario.request_id="local_"+Guid.NewGuid().ToString("N");
            Scenarios.Insert(0,SelectedScenario);if(Scenarios.Count>30)Scenarios.RemoveAt(Scenarios.Count-1);
            SaveLocalHistory();
            if(demo){RequestState="Illustrative example saved locally; real telemetry unchanged";return;}
            Send("scenario",wellId,inputs,headModel);
        }
        public void LoadSaved(string wellId){PrepareLocalHistory();Send("scenarios",wellId,null,false);}
        private void Send(string action,string wellId,GuardianScenarioInputs inputs,bool headModel)
        {
            if(pending!=null){RequestState="Wait for the previous request or its timeout";return;}
            var id=Guid.NewGuid().ToString("N");var b=new StringBuilder("{\"request_id\":");
            AquaTwinSettingsCodec.AppendString(b,id);b.Append(",\"well_id\":");AquaTwinSettingsCodec.AppendString(b,wellId??"");
            b.Append(",\"action\":");AquaTwinSettingsCodec.AppendString(b,action);
            b.Append(",\"sent_at\":").Append(UtcNow.ToString("R",CultureInfo.InvariantCulture));
            if(inputs!=null)
            {
                string encoded=JsonUtility.ToJson(inputs);
                if(!headModel)
                {
                    // Optional physical assumptions are omitted, never sent as zero-valued measurements.
                    encoded="{\"flow_m3_hour\":"+N(inputs.flow_m3_hour)+",\"hours_day\":"+N(inputs.hours_day)+",\"reduction_percent\":"+N(inputs.reduction_percent)+
                        ",\"days\":"+N(inputs.days)+",\"start_day\":"+N(inputs.start_day)+",\"recharge_m3_day\":"+N(inputs.recharge_m3_day)+"}";
                }
                b.Append(",\"inputs\":").Append(encoded);
            }
            b.Append('}');
            var client=GetComponent<HiveMqttWellDataSource>();
            if(client==null||!client.PublishGuardianRequest(b.ToString())){RequestState="Calculated locally; connect HiveMQ and run Guardian to save on the service";return;}
            pending=id;deadline=Time.realtimeSinceStartup+15;RequestState="Waiting for Guardian service…";
        }
        private void Update(){if(pending!=null&&Time.realtimeSinceStartup>deadline){pending=null;RequestState="Guardian did not reply. Local result remains available; check service and topic permissions.";}}
        private static void ApplyPresence(GuardianInsight value,Dictionary<string,object> fields)
        {
            value.HasCriticalCrossing=fields!=null&&fields.TryGetValue("time_to_critical_days",out var c)&&c is double;
            value.HasTdsCrossing=fields!=null&&fields.TryGetValue("time_to_tds_review_days",out var t)&&t is double;
            value.HasDatum=fields!=null&&fields.TryGetValue("reference_elevation_m",out var d)&&d is double&&!string.IsNullOrEmpty(value.vertical_datum)&&!string.IsNullOrEmpty(value.aquifer_id);
        }
        private static string N(double x)=>x.ToString("R",CultureInfo.InvariantCulture);
        private static bool Finite(double x)=>!double.IsNaN(x)&&!double.IsInfinity(x);
        [Serializable] private sealed class ScenarioList{public GuardianScenario[] items;}
        [Serializable] private sealed class ExampleList{public GuardianInsight[] insights;}
        [Serializable] private sealed class ServiceStatus{public string state;public double computed_at;}
        [Serializable] private sealed class Result:GuardianScenario{public GuardianScenario[] scenarios;}
        [Serializable] private sealed class Reply{public string request_id,action,error;public bool ok;public Result result;}
    }
}
