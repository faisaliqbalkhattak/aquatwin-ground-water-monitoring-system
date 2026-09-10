#include <Arduino.h>

// --- Pin Definitions (Matching Your Wokwi Diagram) ---
#define TRIG_PIN     4
#define ECHO_PIN     2
#define TDS_PIN      1

// --- ADC Constraints ---
const float ADC_MAX = 4095.0; // ESP32-S3 12-bit ADC
const float VREF = 3.3;       // Reference voltage

// --- TDS Calibration Parameters ---
const float TDS_ZERO_OFFSET_V = 0.0;      
const float TDS_CALIBRATION_FACTOR = 606.06; 

// --- State Variables for Depletion Rate & Tracking ---
float previous_depth_cm = -1.0;
unsigned long previous_time_ms = 0;

// --- State Variable for pH Dynamics ---
// Changes by 5 units in 8 seconds (0.625 units/sec)
float current_ph = 7.0;

void setup() {
  // Allow hardware power rails to settle
  delay(1000);

  Serial.begin(115200);

  // Ultrasonic sensor setup (Untouched)
  pinMode(TRIG_PIN, OUTPUT);
  pinMode(ECHO_PIN, INPUT);

  // TDS Analog Sensor setup
  pinMode(TDS_PIN, INPUT);

  Serial.println();
  Serial.println("=========================================");
  Serial.println("ESP32-S3 AquaTwin Telemetry Node Started!");
  Serial.println("=========================================");
}

// Function to read ultrasonic distance (Logic strictly preserved)
float readDistanceCm() {
  // Clear the trigger pin
  digitalWrite(TRIG_PIN, LOW);
  delayMicroseconds(2);

  // Send a 10 microsecond HIGH pulse to trigger
  digitalWrite(TRIG_PIN, HIGH);
  delayMicroseconds(10);
  digitalWrite(TRIG_PIN, LOW);

  // Measure response pulse duration (in microseconds)
  long duration = pulseIn(ECHO_PIN, HIGH);

  if (duration == 0) {
    return -1.0; // Sensor timeout / Out of range
  }

  // Calculate distance in cm
  return duration * 0.0343 / 2.0;
}

// Calibrated TDS calculation function
float readTDSPpm() {
  int rawADC = analogRead(TDS_PIN);
  float rawVoltage = (rawADC / ADC_MAX) * VREF;
  
  float correctedVoltage = rawVoltage - TDS_ZERO_OFFSET_V;
  if (correctedVoltage < 0.0) {
    correctedVoltage = 0.0;
  }

  float tdsPpm = correctedVoltage * TDS_CALIBRATION_FACTOR;
  return tdsPpm;
}

// Dynamically updates pH by 5.0 units every 8 seconds (+0.625 pH/sec)
float updateDynamicPH(float delta_t_sec) {
  float ph_rate_per_sec = 5.0 / 8.0; // 0.625 units per second
  current_ph += ph_rate_per_sec * delta_t_sec;

  // Cycle pH back to 6.0 if it exceeds realistic bounds
  if (current_ph > 8.5) {
    current_ph = 6.0;
  }

  return current_ph;
}

void loop() {
  unsigned long current_time_ms = millis();

  // 1. Read depth to water surface using the exact ultrasonic logic
  float depth_cm = readDistanceCm();

  // Handle out-of-range sensor fallback gracefully
  if (depth_cm < 0) {
    depth_cm = previous_depth_cm > 0 ? previous_depth_cm : 200.0;
  }

  // 2. Compute Depletion Rate based on Depth Change: (Current_Depth - Previous_Depth) / Delta_t
  float depletion_rate_cm_s = 0.0;
  float delta_t_sec = 1.0; // Default fallback delta_t

  if (previous_time_ms > 0) {
    delta_t_sec = (current_time_ms - previous_time_ms) / 1000.0;
    if (delta_t_sec > 0 && previous_depth_cm > 0) {
      // Depth increase means water level drops (depletion)
      depletion_rate_cm_s = (depth_cm - previous_depth_cm) / delta_t_sec;
    }
  }

  // Update tracking variables
  previous_depth_cm = depth_cm;
  previous_time_ms = current_time_ms;

  // 3. Read Calibrated Quality Metrics (TDS & Dynamic pH)
  float tds_ppm = readTDSPpm();
  float ph = updateDynamicPH(delta_t_sec);

  // 4. Output Data Contract JSON Payload over Serial (1 Hz frequency)
  Serial.print("{\"depth_cm\":");
  Serial.print(depth_cm, 2);
  Serial.print(",\"depletion_rate_cm_s\":");
  Serial.print(depletion_rate_cm_s > 0 ? depletion_rate_cm_s : 0.0, 4);
  Serial.print(",\"tds_ppm\":");
  Serial.print(tds_ppm, 1);
  Serial.print(",\"ph\":");
  Serial.print(ph, 2);
  Serial.println("}");

  delay(1000); // 1-second interval discrete telemetry
}