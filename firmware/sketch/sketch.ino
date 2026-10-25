#include <Arduino.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <PubSubClient.h>
#include <time.h>
#include <sys/time.h>
#include <esp_sntp.h>
#include <esp_idf_version.h>
#include <math.h>

// ============================================================
// AQUATWIN - GLOBAL WATER HACKATHON 2026
// Electrical Engineer: Hardware Well Firmware
// ============================================================

// -------------------- NODE CONFIGURATION --------------------
//  HW for real harware SIM for simulations from the wokwi, carries not effects other than clarity.
#define WELL_ID "WELL_HW_01"
constexpr double WELL_LAT = 24.453900;
constexpr double WELL_LNG = 54.377300;
constexpr float TOTAL_WELL_DEPTH_CM = 500.0f;

// 1 = Wokwi simulation
// 0 = physical hardware sensor path
#define WOKWI_SIMULATION 0
#define INJECT_SIMULATED_DISTANCE_NOISE 0

// -------------------- WIFI CONFIGURATION --------------------
#if WOKWI_SIMULATION
const char* WIFI_SSID = "Wokwi-GUEST";
const char* WIFI_PASSWORD = "";
#else
const char* WIFI_SSID = "wifi name";
const char* WIFI_PASSWORD = "WIFI PASSWORD";
#endif

// -------------------- HIVEMQ CONFIGURATION --------------------
const char* MQTT_HOST = "1651fcbe155949e089f34c9f4ce8b612.s1.eu.hivemq.cloud";
constexpr uint16_t MQTT_PORT = 8883;
const char* MQTT_USERNAME = "aquatwin_esp32";
const char* MQTT_PASSWORD = "passsword";

// -------------------- PIN DEFINITIONS --------------------
constexpr uint8_t TRIG_PIN = 5;
constexpr uint8_t ECHO_PIN = 6;
constexpr uint8_t TDS_PIN  = 3;

// -------------------- SAMPLING / FILTER SETTINGS --------------------
constexpr uint32_t DISTANCE_SAMPLE_INTERVAL_MS = 100;
constexpr uint32_t PUBLISH_INTERVAL_MS = 3000;
constexpr uint32_t ECHO_TIMEOUT_US = 30000;
constexpr size_t DISTANCE_WINDOW_SIZE = 5;
constexpr uint8_t TDS_SAMPLE_COUNT = 16;
constexpr uint16_t ADC_MAX_VALUE = 4095;

// -------------------- TDS DEMONSTRATION MODEL --------------------
constexpr float TDS_ZERO_OFFSET_V = 0.0f;
constexpr float TDS_CALIBRATION_FACTOR_PPM_PER_V = 606.06f;
constexpr float WOKWI_TDS_PROXY_FULL_SCALE_V = 2.3f;

// -------------------- NETWORK CLIENTS --------------------
WiFiClientSecure secureClient;
PubSubClient mqtt(secureClient);

// -------------------- FILTER STATE --------------------
float distanceWindow[DISTANCE_WINDOW_SIZE] = {0};
size_t distanceCount = 0;
size_t distanceWriteIndex = 0;
float latestRawDistanceCm = NAN;

// -------------------- TIMING STATE --------------------
uint32_t lastDistanceSampleMs = 0;
uint32_t lastPublishMs = 0;
uint32_t lastWiFiAttemptMs = 0;
uint32_t lastMqttAttemptMs = 0;
bool ntpStarted = false;
bool ntpWaitMessageShown = false;

#if WOKWI_SIMULATION
constexpr uint32_t NTP_SYNC_INTERVAL_MS = 15000;
constexpr uint32_t MAX_NTP_SYNC_AGE_MS = 30000;
#else
constexpr uint32_t NTP_SYNC_INTERVAL_MS = 60000;
constexpr uint32_t MAX_NTP_SYNC_AGE_MS = 120000;
#endif

uint32_t lastNtpRequestMs = 0;
volatile bool ntpConfirmed = false;
volatile bool ntpSyncLogPending = false;
volatile uint32_t lastNtpSyncMs = 0;
volatile uint32_t lastNtpSyncEpoch = 0;

// ============================================================
// MQTT TOPIC
// ============================================================
String telemetryTopic()
{
    return String("aquatwin/well/") +
           WELL_ID +
           "/telemetry";
}

// ============================================================
// CHECK MQTT CONFIGURATION
// ============================================================
bool mqttCredentialsConfigured()
{
    return String(MQTT_HOST).length() > 0 &&
           String(MQTT_HOST).indexOf("YOUR_HIVEMQ") < 0 &&
           String(MQTT_USERNAME).length() > 0 &&
           String(MQTT_USERNAME).indexOf("YOUR_MQTT") < 0 &&
           String(MQTT_PASSWORD).length() > 0 &&
           String(MQTT_PASSWORD).indexOf("YOUR_MQTT") < 0 &&
           String(MQTT_PASSWORD).indexOf("PASTE_MQTT_PASSWORD_HERE") < 0;
}

// ============================================================
// WIFI CONNECTION
// ============================================================
void startWiFi()
{
    WiFi.mode(WIFI_STA);
#if WOKWI_SIMULATION
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD, 6);
#else
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
#endif
}

// ============================================================
// START UTC TIME SYNCHRONIZATION
// ============================================================
void onNtpTimeSynchronized(struct timeval* value)
{
    if (value == nullptr || value->tv_sec < 1700000000) return;
    lastNtpSyncEpoch = static_cast<uint32_t>(value->tv_sec);
    lastNtpSyncMs = millis();
    ntpConfirmed = true;
    ntpSyncLogPending = true;
}

void startNtpOnce()
{
    if (ntpSyncLogPending)
    {
        ntpSyncLogPending = false;
        Serial.printf("NTP: confirmed UTC sync, epoch=%lu; next poll interval=%lu ms\n",
            static_cast<unsigned long>(lastNtpSyncEpoch),
            static_cast<unsigned long>(NTP_SYNC_INTERVAL_MS));
    }
    if (WiFi.status() != WL_CONNECTED) return;
    if (ntpStarted && (ntpConfirmed || millis() - lastNtpRequestMs < 30000)) return;
    
    lastNtpRequestMs = millis();
#if ESP_IDF_VERSION_MAJOR >= 5
    esp_sntp_set_time_sync_notification_cb(onNtpTimeSynchronized);
#else
    sntp_set_time_sync_notification_cb(onNtpTimeSynchronized);
#endif
    configTime(0, 0, "pool.ntp.org", "time.google.com", "time.cloudflare.com");

#if ESP_IDF_VERSION_MAJOR >= 5
    esp_sntp_set_sync_interval(NTP_SYNC_INTERVAL_MS);
#else
    sntp_set_sync_interval(NTP_SYNC_INTERVAL_MS);
#endif
    ntpStarted = true;
    Serial.printf("NTP: requested UTC synchronization; repeat every %lu ms\n",
        static_cast<unsigned long>(NTP_SYNC_INTERVAL_MS));
}

uint32_t currentUnixTimestamp()
{
    time_t now;
    time(&now);
    if (now < 1700000000) return 0;
    ntpWaitMessageShown = false;
    return static_cast<uint32_t>(now);
}

// ============================================================
// MAINTAIN WIFI AND MQTT CONNECTIONS
// ============================================================
void maintainConnections()
{
    const uint32_t now = millis();
    // ---------------- WIFI ----------------
    if (WiFi.status() != WL_CONNECTED)
    {
        ntpStarted = false;
        if (now - lastWiFiAttemptMs >= 10000 || lastWiFiAttemptMs == 0)
        {
            lastWiFiAttemptMs = now;
            Serial.println("Wi-Fi disconnected; attempting reconnect...");
            startWiFi();
        }
        return;
    }
    startNtpOnce();
    
    // ---------------- MQTT ----------------
    if (mqtt.connected())
    {
        mqtt.loop();
        return;
    }
    if (now - lastMqttAttemptMs < 5000 && lastMqttAttemptMs != 0)
    {
        return;
    }
    lastMqttAttemptMs = now;
    
    if (!mqttCredentialsConfigured())
    {
        Serial.println("MQTT CONFIG NEEDED: set MQTT_HOST, MQTT_USERNAME and MQTT_PASSWORD.");
        return;
    }
    
    String clientId = String("AquaTwin-") + WELL_ID;
    Serial.print("MQTT connecting as ");
    Serial.println(clientId);
    
    if (mqtt.connect(clientId.c_str(), MQTT_USERNAME, MQTT_PASSWORD))
    {
        Serial.println("MQTT: CONNECTED");
        Serial.print("Topic: ");
        Serial.println(telemetryTopic());
    }
    else
    {
        Serial.print("MQTT connection failed, state=");
        Serial.println(mqtt.state());
    }
}

// ============================================================
// READ HC-SR04 DISTANCE
// ============================================================
float readDistanceCm()
{
    digitalWrite(TRIG_PIN, LOW);
    delayMicroseconds(2);
    digitalWrite(TRIG_PIN, HIGH);
    delayMicroseconds(10);
    digitalWrite(TRIG_PIN, LOW);
    
    const unsigned long echoUs = pulseIn(ECHO_PIN, HIGH, ECHO_TIMEOUT_US);
    
    if (echoUs == 0)
    {
        return NAN;
    }
    
    float distanceCm = static_cast<float>(echoUs) * 0.0343f / 2.0f;
    
#if WOKWI_SIMULATION && INJECT_SIMULATED_DISTANCE_NOISE
    distanceCm += static_cast<float>(random(-10, 11)) / 10.0f;
#endif

    if (!isfinite(distanceCm) || distanceCm < 2.0f || distanceCm > 400.0f)
    {
        return NAN;
    }
    return distanceCm;
}

// ============================================================
// ADD DISTANCE SAMPLE TO THE MOVING-AVERAGE WINDOW
// ============================================================
void addDistanceSample(float sampleCm)
{
    if (!isfinite(sampleCm))
    {
        return;
    }
    latestRawDistanceCm = sampleCm;
    distanceWindow[distanceWriteIndex] = sampleCm;
    distanceWriteIndex = (distanceWriteIndex + 1) % DISTANCE_WINDOW_SIZE;
    
    if (distanceCount < DISTANCE_WINDOW_SIZE)
    {
        distanceCount++;
    }
}

// ============================================================
// CALCULATE THE FIVE-SAMPLE MOVING AVERAGE
// ============================================================
float filteredDistanceCm()
{
    if (distanceCount < DISTANCE_WINDOW_SIZE)
    {
        return NAN;
    }
    float sum = 0.0f;
    for (size_t i = 0; i < DISTANCE_WINDOW_SIZE; i++)
    {
        sum += distanceWindow[i];
    }
    return sum / static_cast<float>(DISTANCE_WINDOW_SIZE);
}

// ============================================================
// READ TDS INPUT
// ============================================================
void readTds(uint16_t &rawAverage, float &voltage, float &ppm)
{
    uint32_t rawSum = 0;
#if !WOKWI_SIMULATION
    uint64_t milliVoltSum = 0;
#endif

    for (uint8_t i = 0; i < TDS_SAMPLE_COUNT; i++)
    {
        rawSum += analogRead(TDS_PIN);
#if !WOKWI_SIMULATION
        milliVoltSum += analogReadMilliVolts(TDS_PIN);
#endif
        delayMicroseconds(250);
    }
    
    rawAverage = static_cast<uint16_t>(rawSum / TDS_SAMPLE_COUNT);
    
#if WOKWI_SIMULATION
    voltage = (rawAverage / static_cast<float>(ADC_MAX_VALUE)) * WOKWI_TDS_PROXY_FULL_SCALE_V;
#else
    voltage = (milliVoltSum / static_cast<float>(TDS_SAMPLE_COUNT)) / 1000.0f;
#endif

    float correctedVoltage = voltage - TDS_ZERO_OFFSET_V;
    if (correctedVoltage < 0.0f)
    {
        correctedVoltage = 0.0f;
    }
    ppm = correctedVoltage * TDS_CALIBRATION_FACTOR_PPM_PER_V;
}

// ============================================================
// PUBLISH TELEMETRY TO HIVEMQ
// ============================================================
bool publishTelemetry(float filteredDistance, uint16_t tdsRaw, float tdsVoltage, float tdsPpm)
{
    if (!mqtt.connected())
    {
        return false;
    }
    
    const uint32_t unixTime = currentUnixTimestamp();
    if (unixTime == 0)
    {
        if (!ntpWaitMessageShown)
        {
            Serial.println("Waiting for initial UTC synchronization; not publishing a fake Unix timestamp.");
            ntpWaitMessageShown = true;
        }
        return false;
    }
    
    float waterColumnHeight = TOTAL_WELL_DEPTH_CM - filteredDistance;
    if (waterColumnHeight < 0.0f) waterColumnHeight = 0.0f;
    if (waterColumnHeight > TOTAL_WELL_DEPTH_CM) waterColumnHeight = TOTAL_WELL_DEPTH_CM;
    
    char payload[384];
    const int n = snprintf(
        payload, sizeof(payload),
        "{\"well_id\":\"%s\","
        "\"location\":{\"lat\":%.6f,\"lng\":%.6f},"
        "\"depth_to_water_cm\":%.2f,"
        "\"water_column_height_cm\":%.2f,"
        "\"tds_ppm\":%.1f,"
        "\"status\":\"ONLINE\","
        "\"timestamp\":%lu}",
        WELL_ID, WELL_LAT, WELL_LNG, filteredDistance,
        waterColumnHeight, tdsPpm, static_cast<unsigned long>(unixTime)
    );
    
    if (n <= 0 || n >= static_cast<int>(sizeof(payload)))
    {
        Serial.println("ERROR: JSON payload buffer too small.");
        return false;
    }
    
    const String topic = telemetryTopic();
    const bool ok = mqtt.publish(topic.c_str(), payload, true);
    
    // ---------------- SERIAL DEBUG OUTPUT ----------------
    Serial.println();
    Serial.println("--- AQUATWIN EDGE TELEMETRY ---");
    Serial.printf("Well ID               : %s\n", WELL_ID);
    Serial.printf("Raw distance sample   : %.2f cm\n", latestRawDistanceCm);
    Serial.printf("5-sample mean distance: %.2f cm\n", filteredDistance);
    Serial.printf("Water column height   : %.2f cm (total depth %.1f cm)\n", waterColumnHeight, TOTAL_WELL_DEPTH_CM);
    Serial.printf("TDS proxy ADC average : %u\n", tdsRaw);
    Serial.printf("TDS modeled voltage   : %.3f V\n", tdsVoltage);
    Serial.printf("TDS modeled ppm       : %.1f ppm\n", tdsPpm);
    Serial.printf("UTC Unix timestamp    : %lu\n", static_cast<unsigned long>(unixTime));
    Serial.print("MQTT topic            : ");
    Serial.println(topic);
    Serial.print("Payload               : ");
    Serial.println(payload);
    Serial.print("Publish result        : ");
    Serial.println(ok ? "SUCCESS" : "FAILED");
    Serial.println("--------------------------------");
    
    return ok;
}

// ============================================================
// SETUP
// ============================================================
void setup()
{
    Serial.begin(115200);
    delay(300);
    
    // Sensor pins
    pinMode(TRIG_PIN, OUTPUT);
    pinMode(ECHO_PIN, INPUT);
    pinMode(TDS_PIN, INPUT);
    digitalWrite(TRIG_PIN, LOW);
    
    analogReadResolution(12);
#if !WOKWI_SIMULATION
    analogSetPinAttenuation(TDS_PIN, ADC_11db);
#endif

    randomSeed(static_cast<unsigned long>(analogRead(TDS_PIN)) ^ micros());
    
    secureClient.setInsecure();
    mqtt.setServer(MQTT_HOST, MQTT_PORT);
    mqtt.setBufferSize(1024);
    mqtt.setKeepAlive(30);
    mqtt.setSocketTimeout(5);
    
    Serial.println("\nAquaTwin | Global Water Hackathon | Edge Node");
    Serial.printf("Well ID: %s\n", WELL_ID);
    Serial.printf("Location: %.6f, %.6f\n", WELL_LAT, WELL_LNG);
    Serial.printf("MQTT topic: %s\n", telemetryTopic().c_str());
    Serial.println("Mode: PHYSICAL HARDWARE");
    
    startWiFi();
    const uint32_t start = millis();
    while (WiFi.status() != WL_CONNECTED && millis() - start < 15000)
    {
        delay(250);
        Serial.print('.');
    }
    Serial.println();
    
    if (WiFi.status() == WL_CONNECTED)
    {
        Serial.print("Wi-Fi connected; IP: ");
        Serial.println(WiFi.localIP());
        startNtpOnce();
    }
    else
    {
        Serial.println("Wi-Fi not connected yet; background reconnect will continue.");
    }
}

// ============================================================
// MAIN LOOP
// ============================================================
void loop()
{
    maintainConnections();
    const uint32_t now = millis();
    static uint32_t lastHealthMs = 0;
    
    if (now - lastHealthMs >= 10000) {
        lastHealthMs = now;
        Serial.printf("HEALTH wifi=%s mqtt=%s utc=%lu ntp_confirmed=%s samples=%u/5\n",
            WiFi.status() == WL_CONNECTED ? "UP" : "DOWN",
            mqtt.connected() ? "UP" : "DOWN",
            static_cast<unsigned long>(currentUnixTimestamp()),
            ntpConfirmed ? "YES" : "NO", static_cast<unsigned>(distanceCount));
    }

    // 1. SAMPLE ULTRASONIC DISTANCE
    if (now - lastDistanceSampleMs >= DISTANCE_SAMPLE_INTERVAL_MS)
    {
        lastDistanceSampleMs = now;
        addDistanceSample(readDistanceCm());
    }
    
    // 2. PUBLISH EVERY THREE SECONDS
    if (now - lastPublishMs >= PUBLISH_INTERVAL_MS)
    {
        lastPublishMs = now;
        const float filtered = filteredDistanceCm();
        
        if (!isfinite(filtered))
        {
            Serial.printf("Filter warming up: %u/5 valid distance samples collected.\n",
                static_cast<unsigned>(distanceCount));
        }
        else if (WiFi.status() == WL_CONNECTED && mqtt.connected())
        {
            uint16_t tdsRaw = 0;
            float tdsVoltage = 0.0f;
            float tdsPpm = 0.0f;
            
            readTds(tdsRaw, tdsVoltage, tdsPpm);
            publishTelemetry(filtered, tdsRaw, tdsVoltage, tdsPpm);
        }
        else
        {
            Serial.println("Publish skipped: waiting for Wi-Fi/MQTT connection.");
        }
    }
    delay(2);
}