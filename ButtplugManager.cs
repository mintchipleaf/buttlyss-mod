﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Security;
using System.Security.Permissions;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using ButtplugManaged;

#pragma warning disable CS0618

[module: UnverifiableCode]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace BUTTLYSS
{
    /// <summary>
    /// Handles actions related to buttplug client operation
    /// </summary>
    [BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
    public class ButtplugManager : BaseUnityPlugin
    {
        /// <summary>
        /// Active buttplug client
        /// </summary>
        private ButtplugClient buttplugClient;
        /// <summary>
        /// List of currently connected buttplug devices received from server
        /// </summary>
        private readonly List<ButtplugClientDevice> connectedDevices = new List<ButtplugClientDevice>();

        /// <summary>
        /// Time elapsed since last vibration command sent to server
        /// </summary>
        private float timeSinceVibeUpdate;

        /// <summary>
        /// Multiplier applied to all vibration speeds
        /// </summary>
        private float strengthMultiplier => 0.8f;


        /// <summary>
        /// Sets up method patches
        /// </summary>
        private void Awake() {
            Logger.LogInfo("BUTTLYSS Awake");
            var harmony = new Harmony("BUTTLYSS");
            harmony.PatchAll();
        }

        /// <summary>
        /// Connects to buttplug client
        /// </summary>
        private void Start() {
            Task.Run(ReconnectClient);
        }

        /// <summary>
        /// Handles sending vibration commands to buttplug client, and related local state
        /// </summary>
        private void Update() {
            State.MaxSpeedThisFrame = 0;

            State.VibeDuration += Time.deltaTime;
            timeSinceVibeUpdate += Time.deltaTime;

            if (buttplugClient == null)
                return;
            if (Properties.EmergencyStop)
                State.CurrentSpeed = 0;

            // This shouldn't be run at more than 10hz, bluetooth can't keep up. Repeated commands will be
            // ignored in Buttplug, but quick updates can still cause lag.
            if (timeSinceVibeUpdate > 0.10) {
                foreach (ButtplugClientDevice device in connectedDevices) {
                    if (device.AllowedMessages.ContainsKey("VibrateCmd")) {
                        double vibeAmt = Math.Min(State.CurrentSpeed * strengthMultiplier, 1.0);
                        device.SendVibrateCmd(Math.Min(State.CurrentSpeed * strengthMultiplier, 1.0));

                        if(vibeAmt != 0)
                            Logger.LogInfo(vibeAmt);
                    }
                }

                timeSinceVibeUpdate = 0;
            }

            if (State.VibeDuration > Properties.MaxVibeCommandLength && Properties.InputMode == InputMode.Varied)
                State.CurrentSpeed = 0;
            else if (Properties.InputMode == InputMode.None)
                State.CurrentSpeed = 0;
        }

        /// <summary>
        /// Sets vibration speed
        /// </summary>
        /// <param name="speed">Speed of vibration command from 0 to 1</param>
        public static void Vibrate(float speed) {
            State.ResetVibeDuration();

            // Vibrate at the hightest current speed
            float newSpeed = Mathf.Clamp(speed, 0, 1);
            newSpeed = Mathf.Max(newSpeed, State.MaxSpeedThisFrame);

            State.CurrentSpeed = newSpeed;
        }

        /// <summary>
        /// Activates small vibrations
        /// Used for very subtle inputs (menu button clicks, dashes, etc)
        /// </summary>
        public static void Tap() {
            if (State.CurrentSpeed < Properties.TapSpeed) {
                Vibrate(Properties.TapSpeed);
            }
        }


        # region Buttplug Client

        /// <summary>
        /// Reconnects buttplug client
        /// </summary>
        public void TryRestartClient() {
            Logger.LogInfo("Restarting Buttplug client...");
            Task.Run(ReconnectClient);
        }

        /// <summary>
        /// Returns currently set URI of buttplug server
        /// </summary>
        /// <returns>Buttplug server URI</returns>
        private Uri GetConnectionUri() {
            return new Uri("ws://192.168.1.150:12345/buttplug");
        }

        /// <summary>
        /// Shuts down and stops buttplug client
        /// </summary>
        /// <returns>Async task for ending scans and disconnecting</returns>
        private async Task TryKillClient() {
            if (buttplugClient == null)
                return;

            Logger.LogInfo("Disconnecting from Buttplug server...");
            buttplugClient.DeviceAdded -= AddDevice;
            buttplugClient.DeviceRemoved -= RemoveDevice;
            buttplugClient.ScanningFinished -= ScanningFinished;
            buttplugClient.ErrorReceived -= ErrorReceived;
            buttplugClient.ServerDisconnect -= ServerDisconnect;

            if (buttplugClient.IsScanning)
                await buttplugClient.StopScanningAsync();
            if (buttplugClient.Connected)
                await buttplugClient.DisconnectAsync();

            buttplugClient = null;
        }

        /// <summary>
        /// Kills and recreates buttplug client, then connects it to buttplug server
        /// </summary>
        /// <returns></returns>
        private async Task ReconnectClient() {
            Uri uri = GetConnectionUri();

            await TryKillClient();

            buttplugClient = new ButtplugClient("ATLYSS");
            buttplugClient.DeviceAdded += AddDevice;
            buttplugClient.DeviceRemoved += RemoveDevice;
            buttplugClient.ScanningFinished += ScanningFinished;
            buttplugClient.ErrorReceived += ErrorReceived;
            buttplugClient.ServerDisconnect += ServerDisconnect;

            Logger.LogInfo("Connecting to Buttplug server...");
            try {
                await buttplugClient.ConnectAsync(new ButtplugWebsocketConnectorOptions(uri));

                await Task.Run(buttplugClient.StartScanningAsync);
            }
            catch (Exception ex) {
                Logger.LogError(ex.ToString());
            }
        }

        #endregion


        #region Device Callbacks

        /// <summary>
        /// Adds new buttplug device to connected device list
        /// </summary>
        private void AddDevice(object sender, DeviceAddedEventArgs args) {
            Logger.LogInfo("Device Added: " + args.Device.Name);
            connectedDevices.Add(args.Device);
        }

        /// <summary>
        /// Removes buttplug device from connected device list
        /// </summary>
        private void RemoveDevice(object sender, DeviceRemovedEventArgs args) {
            Logger.LogInfo("Device Removed: " + args.Device.Name);
            connectedDevices.Remove(args.Device);
        }

        /// <summary>
        /// Logs completion of device scan
        /// </summary>
        private void ScanningFinished(object sender, EventArgs args) {
            Logger.LogInfo("Scanning Finished");
        }

        /// <summary>
        /// Logs encountered errors
        /// </summary>
        private void ErrorReceived(object sender, ButtplugExceptionEventArgs args) {
            Logger.LogError("Error: " + args.Exception.Message);
        }

        /// <summary>
        /// Kills buttplug client
        /// </summary>
        private void ServerDisconnect(object sender, EventArgs args) {
            Logger.LogInfo("Server Disconnected");
            Task.Run(TryKillClient);
        }

        #endregion

        /// <summary>
        /// Disconnects from buttplug server
        /// </summary>
        private void OnDestroy() {
            buttplugClient?.DisconnectAsync().Wait();
        }
    }
}