using TMPro;
using UnityEngine;

namespace AquaTwin
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class RegionalDashboardController : MonoBehaviour
    {
        [SerializeField] private WellVisualizer wellVisualizer;
        [SerializeField] private TMP_Text waterPercentText;
        [SerializeField] private TMP_Text depthFromTopText;
        [SerializeField] private TMP_Text tdsValueText;
        [SerializeField] private TMP_Text tdsStatusText;
        [SerializeField] private TMP_Text phValueText;
        [SerializeField] private TMP_Text phStatusText;
        [SerializeField] private RectTransform waterFill;

        [SerializeField] private Color nominalColor = new Color(0.44f, 0.72f, 0.54f);
        [SerializeField] private Color reviewColor = new Color(0.78f, 0.66f, 0.36f);

        private float lastWater = float.NaN;
        private float lastTds = float.NaN;
        private float lastPh = float.NaN;

        private void OnEnable()
        {
            Refresh(true);
        }

        private void Update()
        {
            Refresh(false);
        }

        private void OnValidate()
        {
            Refresh(true);
        }

        private void Refresh(bool force)
        {
            if (wellVisualizer == null)
                return;

            float water = wellVisualizer.WaterDepthPercent;
            float tds = wellVisualizer.TdsLevel;
            float ph = wellVisualizer.PhLevel;
            if (!force && Mathf.Approximately(water, lastWater) &&
                Mathf.Approximately(tds, lastTds) && Mathf.Approximately(ph, lastPh))
                return;

            lastWater = water;
            lastTds = tds;
            lastPh = ph;

            if (waterPercentText != null)
                waterPercentText.SetText("{0:0.0}%", water);
            if (depthFromTopText != null)
            {
                if (wellVisualizer.HasRecordedDepth)
                    depthFromTopText.SetText("DEPTH TO WATER\n{0:0.00} cm below top", wellVisualizer.DepthFromTopCentimeters);
                else
                    depthFromTopText.SetText("DEPTH TO WATER\n{0:0.00} m below top", wellVisualizer.DepthFromTopMeters);
            }
            if (tdsValueText != null)
                tdsValueText.SetText("{0:0}", tds);
            if (phValueText != null)
                phValueText.SetText("{0:0.00}", ph);

            SetStatus(tdsStatusText, tds <= 600f,
                tds <= 600f ? "Within target" : "Review required");
            SetStatus(phStatusText, ph >= 6.5f && ph <= 8.5f,
                ph >= 6.5f && ph <= 8.5f ? "Within target" : "Review required");

            if (waterFill != null)
            {
                Vector2 maximum = waterFill.anchorMax;
                maximum.x = Mathf.Clamp01(water / 100f);
                waterFill.anchorMax = maximum;
            }
        }

        private void SetStatus(TMP_Text target, bool nominal, string message)
        {
            if (target == null)
                return;

            target.text = "●  " + message;
            target.color = nominal ? nominalColor : reviewColor;
        }
    }
}
