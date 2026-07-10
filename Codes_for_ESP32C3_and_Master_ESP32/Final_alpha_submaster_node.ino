#include <Wire.h>
#include <WiFi.h>
#include <esp_now.h>
#include <Adafruit_NeoPixel.h>
#include <Adafruit_Sensor.h>
#include <Adafruit_BN0055.h>
#include <utility/imumaths.h>

// 1. MAC ADDRESS CONFIGURATION
uint8_t masterAddress[] = {0xD4, 0xE9, 0xF4, 0xB1, 0x83, 0xA4};

const int LED_PIN = 2;
#define BATTERY_PIN 2
#define RGB_PIN 5
#define NUMPIXELS 1

Adafruit_NeoPixel pixels(NUMPIXELS, RGB_PIN, NEO_GRB + NEO_KHZ800);
Adafruit_BN0055 bno = Adafruit_BN0055(55, 0x28, &Wire);

bool isConnectedToMaster = false;
float batVoltage = 4.2;
unsigned long lastBatCheck = 0;
unsigned long lastBlinkTime = 0;
unsigned long lastSuccessfulSend = 0;

// 3. 11-NODE DATA STRUCTURES
typedef struct limb_message {
    uint8_t limbID;
    float qW; float qX; float qY; float qZ;
} limb_message;
limb_message incomingLimbData;

// Fat payload structure, Uses a 2D array [11 nodes] [4 quat values]
// Index 0 = Alpha. Index 1-10 = Peripheral Leaves.
typedef struct master_message {
    float quats[11][4];
    uint16_t activeNodes; // Upgraded to 16-bit to hold 11 status flags
} master_message;
master_message payload;

esp_now_peer_info_t peerInfo;

// Heartbeat trackers for 10 peripheral nodes
unsigned long leafBeats[11] = {0};
unsigned long lastPrintTime = 0;

// 4. ESP-NOW CALLBACKS
void OnDataSent(const uint8_t *mac_addr, esp_now_send_status_t status) {
    if (status == ESP_NOW_SEND_SUCCESS) lastSuccessfulSend = millis();
}

// Routes incoming data into the correct array slot based on ID
void OnDataRecv(const uint8_t *mac_addr, const uint8_t *incomingDataBytes, int len) {
    memcpy(&incomingLimbData, incomingDataBytes, sizeof(incomingLimbData));
    uint8_t id = incomingLimbData.limbID;

    if (id >= 1 && id <= 10) {
        payload.quats[id][0] = incomingLimbData.qW;
        payload.quats[id][1] = incomingLimbData.qX;
        payload.quats[id][2] = incomingLimbData.qY;
        payload.quats[id][3] = incomingLimbData.qZ;
        leafBeats[id] = millis();
    }
}

void setup() {
    Serial.begin(115200);
    Wire.begin(); Wire.setClock(400000);
    pixels.begin(); pixels.setBrightness(50); pixels.clear(); pixels.show();
    pinMode(BATTERY_PIN, INPUT); pinMode(LED_PIN, OUTPUT); digitalWrite(LED_PIN, LOW);
    WiFi.mode(WIFI_STA);
    
    if (esp_now_init() != ESP_OK) return;
    esp_now_register_send_cb(OnDataSent);
    esp_now_register_recv_cb(OnDataRecv);
    
    memcpy(peerInfo.peer_addr, masterAddress, 6);
    peerInfo.channel = 0; peerInfo.encrypt = false;
    esp_now_add_peer(&peerInfo);

    if (!bno.begin()) {
        while (1) {
            pixels.setPixelColor(0, pixels.Color(255, 255, 0)); pixels.show(); delay(500);
            pixels.setPixelColor(0, pixels.Color(0, 0, 0)); pixels.show(); delay(500);
        }
    }
    delay(100); bno.setExtCrystalUse(true); digitalWrite(LED_PIN, HIGH);
    lastPrintTime = micros();
}

void loop() {
    unsigned long nowMillis = millis();
    unsigned long now = micros();
    isConnectedToMaster = (nowMillis - lastSuccessfulSend < 500);

    // BATTERY & LED LOGIC
    if (nowMillis - lastBatCheck >= 1000) {
        lastBatCheck = nowMillis;
        long sum = 0;
        for(int i=0; i<20; i++) { sum += analogRead(BATTERY_PIN); delay(1); }
        batVoltage = ((float)sum / 20.0 / 4095.0) * 3.3 * 2.0;
    }

    if (batVoltage < 3.2) {
        pixels.setPixelColor(0, pixels.Color(255, 0, 0));
    } else {
        uint32_t baseColor = isConnectedToMaster ? pixels.Color(0, 0, 255) : pixels.Color(0, 255, 0);
        if (batVoltage >= 3.2 && batVoltage < 3.6) {
            if (nowMillis - lastBlinkTime > 1500) lastBlinkTime = nowMillis;
            if (nowMillis - lastBlinkTime > 1000) pixels.setPixelColor(0, pixels.Color(255, 0, 0));
            else pixels.setPixelColor(0, baseColor);
        } else {
            pixels.setPixelColor(0, baseColor);
        }
    }
    pixels.show();

    // SENSOR READING & TRANSMISSION (100Hz)
    if (now - lastPrintTime > 10000) {
        lastPrintTime = now;
        
        // Read local Chest BN0055 into Index 0
        imu::Quaternion quat = bno.getQuat();
        payload.quats[0][0] = quat.w(); payload.quats[0][1] = quat.x();
        payload.quats[0][2] = quat.y(); payload.quats[0][3] = quat.z();

        // Construct 16-bit status mask
        payload.activeNodes = 1; // Alpha (Bit 0) is always 1
        for (int i=1; i<=10; i++) {
            if (nowMillis - leafBeats[i] < 500) {
                payload.activeNodes |= (1 << i);
            }
        }

        // Send the fat payload to the Master
        esp_now_send(masterAddress, (uint8_t *) &payload, sizeof(payload));
    }
}