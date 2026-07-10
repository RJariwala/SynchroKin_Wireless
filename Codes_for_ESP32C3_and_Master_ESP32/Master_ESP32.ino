#include <esp_now.h>
#include <WiFi.h>

const int BUILTIN_LED = 2;

// 1. DATA STRUCTURE (MUST MATCH ALPHA)
typedef struct master_message {
    float quats[11][4];
    uint16_t activeNodes;
} master_message;

master_message incomingData;
volatile bool newDataReady = false;

// Array of prefixes for the Unity String Parser
const char* nodePrefixes[11] = {
    "A", "B", "G", "D", "E", "Z", "H", "T", "I", "K", "L"
};

// 2. ESP-NOW RECEIVE CALLBACK
void OnDataRecv(const uint8_t *mac_addr, const uint8_t *incomingDataBytes, int len) {
    if (len == sizeof(incomingData)) {
        newDataReady = true;
        memcpy(&incomingData, incomingDataBytes, sizeof(incomingData));
    }
}

void setup() {
    // CRITICAL: Baud rate upgraded to 921600 to support 11-node data density.
    // Unity Synchrokin.cs MUST be updated to 921600.
    Serial.begin(921600);
    pinMode(BUILTIN_LED, OUTPUT);
    digitalWrite(BUILTIN_LED, LOW);
    WiFi.mode(WIFI_STA);
    
    if (esp_now_init() != ESP_OK) return;
    esp_now_register_recv_cb(OnDataRecv);
}

unsigned long lastMasterPrint = 0;

void loop() {
    if (newDataReady) {
        newDataReady = false;
    }

    // Throttle USB serial output to 50fps (every 20ms)
    if (millis() - lastMasterPrint >= 20) {
        lastMasterPrint = millis();
        
        if (Serial && Serial.availableForWrite() > 256) {
            // Print Status Watchdog line
            Serial.print("STAT:");
            for (int i=0; i < 11; i++) {
                int stat = (incomingData.activeNodes & (1 << i)) ? 1 : 0;
                Serial.print(stat);
                if (i < 10) Serial.print(",");
            }
            Serial.println();

            // Dynamically print all 11 nodes for the Unity Parser
            for (int i=0; i < 11; i++) {
                Serial.print(" "); Serial.print(nodePrefixes[i]); Serial.print("W:");
                Serial.print(incomingData.quats[i][0], 4);
                Serial.print(" "); Serial.print(nodePrefixes[i]); Serial.print("X:");
                Serial.print(incomingData.quats[i][1], 4);
                Serial.print(" "); Serial.print(nodePrefixes[i]); Serial.print("Y:");
                Serial.print(incomingData.quats[i][2], 4);
                Serial.print(" "); Serial.print(nodePrefixes[i]); Serial.print("Z:");
                
                if (i == 10) {
                    Serial.println(incomingData.quats[i][3], 4); // Newline on the last node
                } else {
                    Serial.print(incomingData.quats[i][3], 4);
                }
            }
        }
    }
}