#include <Arduino.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <PubSubClient.h>
#include <time.h>
#include "hivemq_root_ca.h"
#include "secrets.h"

// ============================================================
// WIFI CONFIGURATION
// ============================================================
  
const char* WIFI_SSID = AQUATWIN_WIFI_SSID;
const char* WIFI_PASSWORD = AQUATWIN_WIFI_PASSWORD;

// ============================================================
// HIVEMQ CLOUD CONFIGURATION
// ============================================================

const char* MQTT_HOST =
  "6a3cba0faab347c9a1a0c5afcfbd3ca3.s1.eu.hivemq.cloud";

const int MQTT_PORT = 8883;

const char* MQTT_USERNAME = AQUATWIN_MQTT_USERNAME;
const char* MQTT_PASSWORD = AQUATWIN_MQTT_PASSWORD;

// Well metadata agreed for the Wokwi/cloud payload contract.
const char* WELL_ID = "WELL_WOKWI_01";
const float WELL_LATITUDE = 24.453900;
const float WELL_LONGITUDE = 54.377300;
const float WELL_TOTAL_DEPTH_CM = 500.0;

// Built from WELL_ID in setup to guarantee topic/payload identity consistency.
char MQTT_SENSOR_TOPIC[96];

// ============================================================
// MQTT CLIENT
// ============================================================

WiFiClientSecure secureClient;
PubSubClient mqttClient(secureClient);

// ============================================================
// PIN DEFINITIONS
// ============================================================

#define TRIG_PIN 5
#define ECHO_PIN 6
#define TDS_PIN  3

// ============================================================
// ADC CONSTRAINTS
// ============================================================

const float ADC_MAX = 4095.0;
const float VREF = 3.3;

// ============================================================
// TDS CALIBRATION PARAMETERS
// ============================================================

const float TDS_ZERO_OFFSET_V = 0.0;
const float TDS_CALIBRATION_FACTOR = 606.06;

// ============================================================
// STATE VARIABLES
// ============================================================

float previous_depth_cm = -1.0;
unsigned long previous_time_ms = 0;

const size_t DISTANCE_FILTER_SIZE = 5;
float distance_samples[DISTANCE_FILTER_SIZE] = {0};
size_t distance_sample_count = 0;
size_t distance_sample_index = 0;

// ============================================================
// pH STATE
// ============================================================

// Changes by 5 units in 8 seconds
// 0.625 pH units per second

float current_ph = 7.0;

// ============================================================
// MQTT PUBLISH INTERVAL
// ============================================================

const unsigned long MQTT_PUBLISH_INTERVAL = 3000;

unsigned long lastPublishTime = 0;

// ============================================================
// ULTRASONIC SENSOR
// ============================================================

float readDistanceCm() {

  // Clear trigger pin
  digitalWrite(TRIG_PIN, LOW);
  delayMicroseconds(2);

  // Send 10 microsecond trigger pulse
  digitalWrite(TRIG_PIN, HIGH);
  delayMicroseconds(10);
  digitalWrite(TRIG_PIN, LOW);

  // Measure echo
  long duration = pulseIn(ECHO_PIN, HIGH);

  if (duration == 0) {
    return -1.0;
  }

  // Calculate distance
  return duration * 0.0343 / 2.0;
}

float filterDistanceCm(float sample) {

  distance_samples[distance_sample_index] = sample;
  distance_sample_index =
    (distance_sample_index + 1) % DISTANCE_FILTER_SIZE;

  if (distance_sample_count < DISTANCE_FILTER_SIZE) {
    distance_sample_count++;
  }

  float sum = 0.0;

  for (size_t i = 0; i < distance_sample_count; i++) {
    sum += distance_samples[i];
  }

  return sum / distance_sample_count;
}

// ============================================================
// TDS SENSOR
// ============================================================

float readTDSPpm() {

  int rawADC = analogRead(TDS_PIN);

  float rawVoltage =
    (rawADC / ADC_MAX) * VREF;

  float correctedVoltage =
    rawVoltage - TDS_ZERO_OFFSET_V;

  if (correctedVoltage < 0.0) {
    correctedVoltage = 0.0;
  }

  float tdsPpm =
    correctedVoltage * TDS_CALIBRATION_FACTOR;

  return tdsPpm;
}

// ============================================================
// DYNAMIC pH
// ============================================================

float updateDynamicPH(float delta_t_sec) {

  float ph_rate_per_sec = 5.0 / 8.0;

  current_ph +=
    ph_rate_per_sec * delta_t_sec;

  // Cycle pH back to 6.0
  if (current_ph > 8.5) {
    current_ph = 6.0;
  }

  return current_ph;
}

// ============================================================
// WIFI CONNECTION
// ============================================================

void connectWiFi() {

  Serial.println();
  Serial.println("Connecting to Wi-Fi...");

  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

  while (WiFi.status() != WL_CONNECTED) {

    delay(500);

    Serial.print(".");
  }

  Serial.println();
  Serial.println("Wi-Fi connected!");

  Serial.print("ESP32 IP address: ");
  Serial.println(WiFi.localIP());

  Serial.print("Wi-Fi RSSI: ");
  Serial.print(WiFi.RSSI());
  Serial.println(" dBm");
}

// ============================================================
// CLOCK SYNC FOR TLS CERTIFICATE VALIDATION
// ============================================================

void syncClock() {

  Serial.println("Synchronizing clock for TLS verification...");

  configTime(0, 0, "pool.ntp.org", "time.nist.gov");

  struct tm timeInfo;

  if (!getLocalTime(&timeInfo, 30000)) {

    Serial.println("Clock sync failed; refusing insecure MQTT connection.");
    Serial.println("Restarting in 5 seconds...");

    delay(5000);
    ESP.restart();
  }

  Serial.println("Clock synchronized. TLS certificate dates can be verified.");
}

// ============================================================
// MQTT CONNECTION
// ============================================================

void connectMQTT() {

  while (!mqttClient.connected()) {

    Serial.println();
    Serial.println("Connecting to HiveMQ Cloud...");

    // Generate a unique MQTT client ID
    String clientId =
      "well01-esp32-" +
      String((uint32_t)ESP.getEfuseMac(), HEX);

    Serial.print("Client ID: ");
    Serial.println(clientId);

    bool connected =
      mqttClient.connect(
        clientId.c_str(),
        MQTT_USERNAME,
        MQTT_PASSWORD
      );

    if (connected) {

      Serial.println("=========================================");
      Serial.println("Connected to HiveMQ Cloud!");
      Serial.println("=========================================");

      Serial.print("MQTT Topic: ");
      Serial.println(MQTT_SENSOR_TOPIC);

    } else {

      Serial.print("MQTT connection failed. State: ");
      Serial.println(mqttClient.state());

      Serial.println("Retrying in 5 seconds...");

      delay(5000);
    }
  }
}

// ============================================================
// MQTT PUBLISH
// ============================================================

void publishSensorData(
  float depth_to_water_cm,
  float tds_ppm
) {

  // ----------------------------------------------------------
  // JSON PAYLOAD
  // ----------------------------------------------------------

  float water_column_height_cm =
    WELL_TOTAL_DEPTH_CM - depth_to_water_cm;

  if (water_column_height_cm < 0.0) {
    water_column_height_cm = 0.0;
  }

  unsigned long timestamp =
    (unsigned long)time(nullptr);

  char payload[320];

  snprintf(
    payload,
    sizeof(payload),

    "{\"well_id\":\"%s\","
    "\"location\":{\"lat\":%.6f,\"lng\":%.6f},"
    "\"depth_to_water_cm\":%.2f,"
    "\"water_column_height_cm\":%.2f,"
    "\"tds_ppm\":%.1f,"
    "\"status\":\"ONLINE\","
    "\"timestamp\":%lu}",

    WELL_ID,
    WELL_LATITUDE,
    WELL_LONGITUDE,
    depth_to_water_cm,
    water_column_height_cm,
    tds_ppm,
    timestamp
  );

  // ----------------------------------------------------------
  // PRINT TO SERIAL
  // ----------------------------------------------------------

  Serial.println();
  Serial.println("Publishing MQTT message:");

  Serial.println(payload);

  // ----------------------------------------------------------
  // PUBLISH
  // ----------------------------------------------------------

  bool success =
    mqttClient.publish(
      MQTT_SENSOR_TOPIC,
      payload
    );

  if (success) {

    Serial.println("MQTT publish: SUCCESS");

  } else {

    Serial.println("MQTT publish: FAILED");
  }
}

// ============================================================
// SETUP
// ============================================================

void setup() {

  // Allow hardware power rails to settle
  delay(1000);

  Serial.begin(115200);

  // ----------------------------------------------------------
  // SENSOR SETUP
  // ----------------------------------------------------------

  pinMode(TRIG_PIN, OUTPUT);
  pinMode(ECHO_PIN, INPUT);

  pinMode(TDS_PIN, INPUT);

  // ----------------------------------------------------------
  // STARTUP MESSAGE
  // ----------------------------------------------------------

  Serial.println();
  Serial.println("=========================================");

  snprintf(
    MQTT_SENSOR_TOPIC,
    sizeof(MQTT_SENSOR_TOPIC),
    "aquatwin/well/%s/telemetry",
    WELL_ID
  );
  Serial.println("ESP32-S3 AquaTwin Telemetry Node Started!");
  Serial.println("=========================================");

  // ----------------------------------------------------------
  // WIFI
  // ----------------------------------------------------------

  connectWiFi();

  syncClock();

  // ----------------------------------------------------------
  // MQTT TLS
  // ----------------------------------------------------------

  // Verify the broker certificate and hostname against the trusted root CA.
  secureClient.setCACert(HIVEMQ_ROOT_CA);

  mqttClient.setServer(
    MQTT_HOST,
    MQTT_PORT
  );

  // ----------------------------------------------------------
  // MQTT
  // ----------------------------------------------------------

  connectMQTT();

  // ----------------------------------------------------------
  // INITIAL TIMING
  // ----------------------------------------------------------

  previous_time_ms = millis();
  lastPublishTime = millis();
}

// ============================================================
// MAIN LOOP
// ============================================================

void loop() {

  // ----------------------------------------------------------
  // KEEP MQTT CONNECTION ALIVE
  // ----------------------------------------------------------

  if (!mqttClient.connected()) {

    connectMQTT();
  }

  mqttClient.loop();

  // ----------------------------------------------------------
  // CONTROL PUBLISH RATE
  // ----------------------------------------------------------

  unsigned long current_time_ms =
    millis();

  if (
    current_time_ms - lastPublishTime
    >= MQTT_PUBLISH_INTERVAL
  ) {

    lastPublishTime =
      current_time_ms;

    // --------------------------------------------------------
    // TIME DELTA FOR pH
    // --------------------------------------------------------

    float delta_t_sec =
      (current_time_ms - previous_time_ms)
      / 1000.0;

    previous_time_ms =
      current_time_ms;

    // --------------------------------------------------------
    // READ ULTRASONIC SENSOR
    // --------------------------------------------------------

    float depth_cm =
      readDistanceCm();

    // --------------------------------------------------------
    // SENSOR FALLBACK
    // --------------------------------------------------------

    if (depth_cm < 0) {

      if (previous_depth_cm > 0) {

        depth_cm =
          previous_depth_cm;

      } else {

        depth_cm =
          200.0;
      }

      Serial.println(
        "HC-SR04 timeout - using previous value"
      );
    }

    // Smooth ultrasonic noise with the required 5-sample moving average.
    depth_cm = filterDistanceCm(depth_cm);

    // --------------------------------------------------------
    // SAVE FILTERED DEPTH
    // --------------------------------------------------------

    previous_depth_cm =
      depth_cm;

    // --------------------------------------------------------
    // READ TDS
    // --------------------------------------------------------

    float tds_ppm =
      readTDSPpm();

    // --------------------------------------------------------
    // UPDATE pH
    // --------------------------------------------------------

    float ph =
      updateDynamicPH(delta_t_sec);

    // --------------------------------------------------------
    // SERIAL MONITOR
    // --------------------------------------------------------

    Serial.println();
    Serial.println("-----------------------------------------");

    Serial.print("Distance: ");
    Serial.print(depth_cm, 2);
    Serial.println(" cm");

    Serial.print("TDS: ");
    Serial.print(tds_ppm, 2);
    Serial.println(" ppm");

    Serial.print("pH: ");
    Serial.println(ph, 2);

    // --------------------------------------------------------
    // MQTT
    // --------------------------------------------------------

    publishSensorData(
      depth_cm,
      tds_ppm
    );

    Serial.println("-----------------------------------------");
  }
}
