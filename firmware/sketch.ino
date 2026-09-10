#include <Arduino.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <PubSubClient.h>

// ============================================================
// WIFI CONFIGURATION
// ============================================================
  
const char* WIFI_SSID = "YOUR_WIFI_SSID";
const char* WIFI_PASSWORD = "YOUR_WIFI_PASSWORD";

// ============================================================
// HIVEMQ CLOUD CONFIGURATION
// ============================================================

const char* MQTT_HOST =
  "6a3cba0faab347c9a1a0c5afcfbd3ca3.s1.eu.hivemq.cloud";

const int MQTT_PORT = 8883;

const char* MQTT_USERNAME = "YOUR_MQTT_USERNAME";
const char* MQTT_PASSWORD = "YOUR_MQTT_PASSWORD";

// MQTT topic
const char* MQTT_SENSOR_TOPIC = "digitaltwin/well01/sensors";

// ============================================================
// MQTT CLIENT
// ============================================================

WiFiClientSecure secureClient;
PubSubClient mqttClient(secureClient);

// ============================================================
// PIN DEFINITIONS
// ============================================================

#define TRIG_PIN 4
#define ECHO_PIN 2
#define TDS_PIN  1

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

// ============================================================
// pH STATE
// ============================================================

// Changes by 5 units in 8 seconds
// 0.625 pH units per second

float current_ph = 7.0;

// ============================================================
// MQTT PUBLISH INTERVAL
// ============================================================

const unsigned long MQTT_PUBLISH_INTERVAL = 2000;

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
  float distance_cm,
  float tds_ppm,
  float ph
) {

  // ----------------------------------------------------------
  // JSON PAYLOAD
  // ----------------------------------------------------------

  char payload[256];

  snprintf(
    payload,
    sizeof(payload),

    "{\"device\":\"well01\","
    "\"distance_cm\":%.2f,"
    "\"tds_ppm\":%.2f,"
    "\"ph\":%.2f}",

    distance_cm,
    tds_ppm,
    ph
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
  Serial.println("ESP32-S3 AquaTwin Telemetry Node Started!");
  Serial.println("=========================================");

  // ----------------------------------------------------------
  // WIFI
  // ----------------------------------------------------------

  connectWiFi();

  // ----------------------------------------------------------
  // MQTT TLS
  // ----------------------------------------------------------

  // DEVELOPMENT MODE:
  // Accept HiveMQ's TLS certificate without CA validation.
  //
  // This is what you were already using successfully.
  // We can replace this later with proper CA validation.

  secureClient.setInsecure();

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

    // --------------------------------------------------------
    // SAVE VALID DEPTH
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
      tds_ppm,
      ph
    );

    Serial.println("-----------------------------------------");
  }
}