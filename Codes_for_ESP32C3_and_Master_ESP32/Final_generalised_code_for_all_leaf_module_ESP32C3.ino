#include <Wire.h>
#include <WiFi.h>
#include <esp_now.h>
#include <Adafruit_NeoPixel.h>
#include <Adafruit_Sensor.h>
#include <Adafruit_BN0055.h>
#include <utility/imumaths.h>

// 1. MAC ADDRESS CONFIGURATION
// This must be the MAC address of your ALPHA (Chest) Relay Hub.
uint8_t alphaAddress[] = {0x1C, 0xDB, 0xD4, 0xED, 0x09, 0xFC};

// 2. NODE IDENTITY SETUP
// Assign a unique ID before flashing to a new board:
// 1-Beta, 2-Gamma, 3-Delta, 4-Epsilon, 5-Zeta, 6-Eta, 7-Theta, 8-Iota, 9-Kappa, 10-Lambda
const uint8_t MY_LIMB_ID = 1;

// 3. HARDWARE PINS & SETUP
const int LED_PIN = 2;
#define BATTERY_PIN 2 // ADC pin reading the voltage divider
#define RGB_PIN 5
#define NUMPIXELS 1

// Data pin for WS2812B NeoPixel
Adafruit_NeoPixel pixels(NUMPIXELS, RGB_PIN, NEO_GRB + NEO_KHZ800);
Adafruit_BN0055 bno = Adafruit_BN0055(55, 0x28, &Wire);

// 4. SYSTEM STATUS VARIABLES
bool isConnected = false;
float batVoltage = 4.2;
unsigned long lastBatCheck = 0;
unsigned long lastBlinkTime = 0;
unsigned long lastSuccessfulSend = 0;

typedef struct limb_message {
    uint8_t limbID;
    float qW; float qX; float qY; float qZ;
} limb_message;

limb_message myData;
esp_now_peer_info_t peerInfo;
unsigned long lastPrintTime = 0;

// 5. ESP-NOW TRANSMIT CALLBACK
void OnDataSent(const uint8_t *mac_addr, esp_now_send_status_t status) {
    if (status == ESP_NOW_SEND_SUCCESS) {
        lastSuccessfulSend = millis();
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
    memcpy(peerInfo.peer_addr, alphaAddress, 6);
    peerInfo.channel = 0; peerInfo.encrypt = false;
    esp_now_add_peer(&peerInfo);
    
    if (!bno.begin()) {
        while (1) { // Hardware failure lock
            pixels.setPixelColor(0, pixels.Color(255, 255, 0)); pixels.show(); delay(500);
            pixels.setPixelColor(0, pixels.Color(0, 0, 0)); pixels.show(); delay(500);
        }
    }
    delay(100); bno.setExtCrystalUse(true); digitalWrite(LED_PIN, HIGH);
    
    myData.limbID = MY_LIMB_ID;
    lastPrintTime = micros();
}

void loop() {
    unsigned long nowMillis = millis();
    unsigned long now = micros();
    isConnected = (nowMillis - lastSuccessfulSend < 500);
    
    // BATTERY LOGIC
    if (nowMillis - lastBatCheck > 1000) {
        lastBatCheck = nowMillis;
        long sum = 0;
        for(int i=0; i<20; i++) { sum += analogRead(BATTERY_PIN); delay(1); }
        batVoltage = ((float)sum / 20.0 / 4095.0) * 3.3 * 2.0;
    }

    // DIAGNOSTIC LED LOGIC
    if (batVoltage < 3.2) {
        pixels.setPixelColor(0, pixels.Color(255, 0, 0));
    } else {
        uint32_t baseColor = isConnected ? pixels.Color(0, 0, 255) : pixels.Color(0, 255, 0);
        if (batVoltage >= 3.2 && batVoltage < 3.6) {
            if (nowMillis - lastBlinkTime > 1500) lastBlinkTime = nowMillis;
            if (nowMillis - lastBlinkTime > 1000) pixels.setPixelColor(0, pixels.Color(255, 0, 0));
            else pixels.setPixelColor(0, baseColor);
        } else {
            pixels.setPixelColor(0, baseColor);
        }
    }
    pixels.show();

    // TRANSMISSION LOGIC (100Hz)
    if (now - lastPrintTime > 10000) {
        lastPrintTime = now;
        imu::Quaternion quat = bno.getQuat();
        myData.qW = quat.w(); myData.qX = quat.x(); myData.qY = quat.y(); myData.qZ = quat.z();
        esp_now_send(alphaAddress, (uint8_t *) &myData, sizeof(myData));
    }
}