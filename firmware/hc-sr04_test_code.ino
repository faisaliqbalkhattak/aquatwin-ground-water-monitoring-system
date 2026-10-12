#include <Arduino.h>

#define ECHO_PIN 2
#define TRIG_PIN 4

void setup() {
  Serial.begin(115200);
  pinMode(TRIG_PIN, OUTPUT);
  pinMode(ECHO_PIN, INPUT);
  
  Serial.println("ESP32-S3 HC-SR04 Distance Test Started...");
}

void loop() {
  // Clear the trigger pin
  digitalWrite(TRIG_PIN, LOW);
  delayMicroseconds(2);

  // Send a 10 microsecond HIGH pulse to trigger
  digitalWrite(TRIG_PIN, HIGH);
  delayMicroseconds(10);
  digitalWrite(TRIG_PIN, LOW);

  // Measure response pulse duration (in microseconds)
  long duration = pulseIn(ECHO_PIN, HIGH);

  // Calculate distance in cm and inches
  float distanceCm = duration * 0.0343 / 2.0;
  float distanceInch = distanceCm / 2.54;

  // Output to Serial Monitor
  if (duration == 0) {
    Serial.println("No echo received / Out of range");
  } else {
    Serial.print("Distance: ");
    Serial.print(distanceCm, 1);
    Serial.print(" cm  |  ");
    Serial.print(distanceInch, 1);
    Serial.println(" inches");
  }

  delay(1000); // Wait 1 second before next reading
}