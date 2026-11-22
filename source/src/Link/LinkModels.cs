using System.Collections.Generic;

namespace LinkPilot.Link
{
    public enum LinkKind { Fan, Pump, Sensor, Other }

    public sealed class LinkModelInfo
    {
        public byte Model;
        public string Name;
        public LinkKind Kind;
        public bool CanControlSpeed;
        public string TempLabel;

        public LinkModelInfo(byte model, string name, LinkKind kind, bool canControl, string tempLabel = "sensor")
        {
            Model = model; Name = name; Kind = kind; CanControlSpeed = canControl; TempLabel = tempLabel;
        }
    }

    /// <summary>
    /// iCUE LINK device type codes, as reported by the hub's device list.
    /// Collected from the open-source FanControl.CorsairLink and OpenLinkHub projects.
    /// </summary>
    public static class LinkModels
    {
        private static readonly Dictionary<byte, LinkModelInfo> Known = new Dictionary<byte, LinkModelInfo>
        {
            { 0x01, new LinkModelInfo(0x01, "QX RGB Fan", LinkKind.Fan, true, "air") },
            { 0x02, new LinkModelInfo(0x02, "LX RGB Fan", LinkKind.Fan, true) },
            { 0x03, new LinkModelInfo(0x03, "RX RGB MAX Fan", LinkKind.Fan, true) },
            { 0x04, new LinkModelInfo(0x04, "RX MAX Fan", LinkKind.Fan, true) },
            { 0x05, new LinkModelInfo(0x05, "LINK Adapter", LinkKind.Other, false) },
            { 0x07, new LinkModelInfo(0x07, "H-Series AIO Pump", LinkKind.Pump, true, "liquid") },
            { 0x09, new LinkModelInfo(0x09, "XC7 CPU Block", LinkKind.Sensor, false, "coolant") },
            { 0x0a, new LinkModelInfo(0x0a, "XG3 Hybrid GPU Block", LinkKind.Fan, true) },
            { 0x0b, new LinkModelInfo(0x0b, "HXi SHIFT PSU", LinkKind.Other, false) }, // PSU fan stays PSU-managed
            { 0x0c, new LinkModelInfo(0x0c, "XD5 Pump/Res", LinkKind.Pump, true, "coolant") },
            { 0x0d, new LinkModelInfo(0x0d, "XG7 GPU Block", LinkKind.Other, false) },
            { 0x0e, new LinkModelInfo(0x0e, "XD5 Elite LCD Pump/Res", LinkKind.Pump, true, "coolant") },
            { 0x0f, new LinkModelInfo(0x0f, "RX RGB Fan", LinkKind.Fan, true) },
            { 0x10, new LinkModelInfo(0x10, "VRM Fan Module", LinkKind.Fan, true) },
            { 0x11, new LinkModelInfo(0x11, "TITAN AIO Pump", LinkKind.Pump, true, "liquid") },
            { 0x13, new LinkModelInfo(0x13, "RX Fan", LinkKind.Fan, true) },
            { 0x19, new LinkModelInfo(0x19, "XD6 Pump/Res", LinkKind.Pump, true, "coolant") },
            { 0x1b, new LinkModelInfo(0x1b, "COMMANDER DUO", LinkKind.Fan, true) },
        };

        public static LinkModelInfo Lookup(byte model)
        {
            if (Known.TryGetValue(model, out var info))
            {
                return info;
            }
            // Unknown device: allow control, but treat it like a pump so it can never be stopped.
            return new LinkModelInfo(model, $"Unknown LINK device (0x{model:x2})", LinkKind.Pump, true);
        }

        public static bool IsKnown(byte model) => Known.ContainsKey(model);
    }
}
