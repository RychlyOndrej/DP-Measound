#include <Arduino.h>
#include <AccelStepper.h>
#include <esp_task_wdt.h> // ESP32 Watchdog Timer

/* ====================================================================
 * 1. KONFIGURACE HARDWARU (ESP32)
 * ====================================================================
 */
const int enPin  = 18; 
const int stpPin = 16; 
const int dirPin = 17; 

// 1/64 Microstepping = 12800 kroků na celou otáčku
const float stepsPerRev = 12800.0; 

/* ====================================================================
 * 2. NASTAVENÍ FYZIKY POHYBU (Soft Start & Precision)
 * ====================================================================
 */
const float targetMaxSpeed = 20000.0;  
const float targetAccel = 2000.0;      

// Watchdog timeout v sekundách
const int WDT_TIMEOUT = 3; 

/* ====================================================================
 * 3. INICIALIZACE OBJEKTŮ A PROMĚNNÝCH
 * ====================================================================
 */
AccelStepper stepper(AccelStepper::DRIVER, stpPin, dirPin);

// Interní proměnné pro polohu
float currentTargetAngle = 0.0; 
String currentAngleString = "0"; // Pamatuje si přesný string od PC pro potvrzení

bool isMoving = false;
bool emergencyStop = false;

// Proměnné pro neblokující čtení sériové linky
const byte numChars = 64;
char receivedChars[numChars];
bool newData = false;

void setup() {
  Serial.begin(115200);
  
  pinMode(enPin, OUTPUT);
  digitalWrite(enPin, LOW); // LOW = Motor drží

  stepper.setMaxSpeed(targetMaxSpeed);     
  stepper.setAcceleration(targetAccel);
  stepper.setCurrentPosition(0);

  // Inicializace Watchdog Timeru pro nové jádro ESP32 (v3.x)
  esp_task_wdt_config_t twdt_config = {
      .timeout_ms = WDT_TIMEOUT * 1000,                // Nové API vyžaduje milisekundy
      .idle_core_mask = (1 << portNUM_PROCESSORS) - 1, // Hlídá všechna dostupná jádra
      .trigger_panic = true                            // Při záseku provede tvrdý reset
  };
  
  esp_task_wdt_init(&twdt_config);
  esp_task_wdt_add(NULL); // Přidá aktuální vlákno (hlavní smyčku) pod dohled
  
  delay(500);
  Serial.println("READY");
}

/* ====================================================================
 * 4. NEBLOKUJÍCÍ ČTENÍ DAT
 * ====================================================================
 */
void receiveSerialData() {
  static byte ndx = 0;
  char endMarker = '\n';
  char rc;
  
  while (Serial.available() > 0 && !newData) {
    rc = Serial.read();

    if (rc != endMarker && rc != '\r') {
      receivedChars[ndx] = rc;
      ndx++;
      if (ndx >= numChars) {
        ndx = numChars - 1; // Ochrana proti přetečení bufferu
      }
    } 
    else if (rc == endMarker) {
      receivedChars[ndx] = '\0'; 
      ndx = 0;
      newData = true;
    }
  }
}

/* ====================================================================
 * 5. ZPRACOVÁNÍ PŘÍKAZŮ
 * ====================================================================
 */
void processCommand() {
  if (!newData) return;
  
  String input = String(receivedChars);
  input.trim();
  newData = false; // Reset flagu pro příjem dalších dat

  if (input.length() == 0) return;

  // --- A) BEZPEČNOSTNÍ STOP ---
  if (input == "STOP") {
    stepper.stop(); 
    emergencyStop = true;
    isMoving = false;
    
    // Záchrana aktuální pozice z AccelStepperu zpět do paměti úhlu
    currentTargetAngle = (stepper.currentPosition() / stepsPerRev) * 360.0;
    
    Serial.println("ACK_STOP");
    return;
  }

  // --- B) KOMUNIKACE A ŘÍZENÍ HARDWARU ---
  if (input == "PING") {
    Serial.println("PONG");
    return;
  }
  else if (input == "EN_OFF") {
    digitalWrite(enPin, HIGH); 
    stepper.disableOutputs();
    Serial.println("ACK_EN_OFF");
    return;
  }
  else if (input == "EN_ON") {
    digitalWrite(enPin, LOW);  
    stepper.enableOutputs();
    Serial.println("ACK_EN_ON");
    return;
  }
  else if (input == "RESET") {
    stepper.setCurrentPosition(0);
    currentTargetAngle = 0.0;
    currentAngleString = "0";
    emergencyStop = false;
    isMoving = false;
    Serial.println("ACK_RESET");
    return;
  }

  // --- C) POHYB ---
  if (input.startsWith("GOTO:")) {
    emergencyStop = false;
    String valStr = input.substring(5);
    
    // Náhrada čárky za tečku
    valStr.replace(',', '.'); 
    
    // Uložíme přesný text úhlu pro późnější 1:1 potvrzení do PC
    currentAngleString = valStr;
    
    currentTargetAngle = valStr.toFloat();
    
    // Algoritmus absolutní přesnosti
    long targetStepAbs = round((currentTargetAngle / 360.0) * stepsPerRev);
    stepper.moveTo(targetStepAbs);
    
    isMoving = true;
    
    // Pokud už jsme na cílové pozici, rovnou pošleme potvrzení
    if (stepper.distanceToGo() == 0) {
      isMoving = false;
      Serial.print("DONE:");
      Serial.println(currentAngleString);
    }
  }
}

/* ====================================================================
 * 6. HLAVNÍ SMYČKA
 * ====================================================================
 */
void loop() {
  esp_task_wdt_reset(); // Reset Watchdogu (dává najevo, že program nezamrzl)

  receiveSerialData();
  processCommand();
  
  stepper.run();

  // Jakmile motor dojede do cíle, zašle PC transakční potvrzení (ACK)
  if (isMoving && !emergencyStop) {
    if (stepper.distanceToGo() == 0) {
      isMoving = false;
      Serial.print("DONE:");
      Serial.println(currentAngleString); 
    }
  }
}