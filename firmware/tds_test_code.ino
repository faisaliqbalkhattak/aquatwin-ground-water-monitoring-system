void setup() {
  Serial.begin(115200);
  pinMode(1, INPUT);
}

void loop() {
  int rawADC = analogRead(1);
  float voltage = (rawADC / 4095.0) * 3.3;
  Serial.print("Raw ADC: "); Serial.print(rawADC);
  Serial.print(" | Voltage: "); Serial.println(voltage, 3);
  delay(1000);
}