using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Foundation;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Web.Http;

namespace Kazyx.DeviceDiscovery
{
    public class SsdpDiscovery
    {
        private const string MULTICAST_ADDRESS = "239.255.255.250";

        private readonly HostName MULTICAST_HOST = new HostName(MULTICAST_ADDRESS);

        private const int SSDP_PORT = 1900;
        private const int RESULT_BUFFER = 8192;

        private const int SSDP_RES_MAX_READ_LINES = 20;

        public const string ST_ALL = "ssdp:all";

        private uint _MX = 1;
        public uint MX
        {
            set { _MX = value; }
            get { return _MX; }
        }

        private readonly TimeSpan DEFAULT_TIMEOUT = new TimeSpan(0, 0, 5);

        public delegate void SonyCameraDeviceHandler(object sender, SonyCameraDeviceEventArgs e);

        public event SonyCameraDeviceHandler SonyCameraDeviceDiscovered;

        protected void OnDiscovered(SonyCameraDeviceEventArgs e)
        {
            if (SonyCameraDeviceDiscovered != null)
            {
                SonyCameraDeviceDiscovered(this, e);
            }
        }

        public delegate void DeviceDescriptionHandler(object sender, DeviceDescriptionEventArgs e);

        public event DeviceDescriptionHandler DescriptionObtained;

        protected void OnDiscovered(DeviceDescriptionEventArgs e)
        {
            if (DescriptionObtained != null)
            {
                DescriptionObtained(this, e);
            }
        }

        public event EventHandler Finished;

        protected void OnTimeout(EventArgs e)
        {
            if (Finished != null)
            {
                Finished(this, e);
            }
        }

        private async void Search(string st, TimeSpan? timeout = null)
        {
            Log("Search");

            var ssdp_data = new StringBuilder()
                .Append("M-SEARCH * HTTP/1.1").Append("\r\n")
                .Append("HOST: ").Append(MULTICAST_ADDRESS).Append(":").Append(SSDP_PORT.ToString()).Append("\r\n")
                .Append("MAN: ").Append("\"ssdp:discover\"").Append("\r\n")
                .Append("MX: ").Append(MX.ToString()).Append("\r\n")
                .Append("ST: ").Append(st).Append("\r\n")
                .Append("\r\n")
                .ToString();

            var adapters = TargetNetworkAdapters ?? await GetActiveAdaptersAsync().ConfigureAwait(false);

            await Task.WhenAll(adapters.Select(async adapter =>
            {
                using (var socket = new DatagramSocket())
                {
                    socket.Control.DontFragment = true;

                    var handler = new TypedEventHandler<DatagramSocket, DatagramSocketMessageReceivedEventArgs>((sender, args) =>
                    {
                        Log("Datagram message received");
                        if (args == null)
                        {
                            return;
                        }
                        string data;
                        using (var reader = args.GetDataReader())
                        {
                            data = reader.ReadString(reader.UnconsumedBufferLength);
                        }
                        Log(data);
                        Task task = GetDeviceDescriptionAsync(data, args.LocalAddress);
                    });

                    socket.MessageReceived += handler;

                    try
                    {
                        await socket.BindServiceNameAsync("", adapter);
                        socket.JoinMulticastGroup(MULTICAST_HOST);

                        using (var output = await socket.GetOutputStreamAsync(MULTICAST_HOST, SSDP_PORT.ToString()))
                        {
                            var buffer = Encoding.UTF8.GetBytes(ssdp_data).AsBuffer();
                            await output.WriteAsync(buffer);
                        }
                        await Task.Delay(timeout ?? DEFAULT_TIMEOUT).ConfigureAwait(false);
                        Log("Search Timeout");
                    }
                    catch (Exception e)
                    {
                        Log("Failed to send multicast: " + e.StackTrace);
                    }

                    socket.MessageReceived -= handler;
                    await socket.CancelIOAsync();
                }
            })).ConfigureAwait(false);

            OnTimeout(new EventArgs());
        }

        public IList<NetworkAdapter> TargetNetworkAdapters
        {
            set; get;
        }

        public static Task<IList<NetworkAdapter>> GetActiveAdaptersAsync()
        {
            var tcs = new TaskCompletionSource<IList<NetworkAdapter>>();

            Task.Run(() =>
            {
                var profiles = NetworkInformation.GetConnectionProfiles();
                var list = new List<NetworkAdapter>();
                foreach (var profile in profiles)
                {
                    if (profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
                    {
                        // Historical profiles.
                        // Log("ConnectivityLevel None: " + profile.ProfileName);
                        continue;
                    }

                    var adapter = profile.NetworkAdapter;

                    switch (adapter.IanaInterfaceType)
                    {
                        case 6: // Ethernet
                        case 71: // 802.11
                            break;
                        default:
                            // Log("Type mismatch: " + profile.ProfileName);
                            continue;
                    }

                    if (!list.Contains(adapter))
                    {
                        Log("Active Adapter: " + profile.ProfileName);
                        list.Add(adapter);
                    }
                }

                tcs.SetResult(list);
            });

            return tcs.Task;
        }

        /// <summary>
        /// Search sony camera devices and retrieve the endpoint URLs.
        /// </summary>
        /// <param name="timeout">Timeout to end up search.</param>
        public void SearchSonyCameraDevices(TimeSpan? timeout = null)
        {
            Search("urn:schemas-sony-com:service:ScalarWebAPI:1", timeout);
        }

        /// <summary>
        /// Search UPnP devices and retrieve the device description.
        /// </summary>
        /// <param name="st">Search Target parameter for SSDP.</param>
        /// <param name="timeout">Timeout to end up search.</param>
        public void SearchUpnpDevices(string st = ST_ALL, TimeSpan? timeout = null)
        {
            if (string.IsNullOrEmpty(st))
            {
                st = ST_ALL;
            }
            Search(st, timeout);
        }

        private HttpClient HttpClient = new HttpClient();

        private Dictionary<Uri, string> _cache = new Dictionary<Uri, string>();

        public void ClearCache()
        {
            _cache.Clear();
        }

        private async Task GetDeviceDescriptionAsync(string data, HostName remoteAddress)
        {
            var dd_location = ParseLocation(data);
            if (dd_location != null)
            {
                try
                {
                    var uri = new Uri(dd_location);
                    if (_cache.ContainsKey(uri))
                    {
                        Log("Cache hit: " + uri);
                        OnDescriptionObtained(_cache[uri], uri, remoteAddress);
                        return;
                    }

                    using (var res = await HttpClient.GetAsync(uri))
                    {
                        if (res.IsSuccessStatusCode)
                        {
                            var response = await res.Content.ReadAsStringAsync();
                            _cache.Add(uri, response);
                            OnDescriptionObtained(response, uri, remoteAddress);
                        }
                    }
                }
                catch (Exception)
                {
                    //Invalid DD location.
                }
            }
        }

        private void OnDescriptionObtained(string response, Uri uri, HostName remoteAddress)
        {
            OnDiscovered(new DeviceDescriptionEventArgs(response, uri, remoteAddress));

            var camera = AnalyzeDescription(response);
            if (camera != null)
            {
                OnDiscovered(new SonyCameraDeviceEventArgs(camera, uri, remoteAddress));
            }
        }

        private static string ParseLocation(string response)
        {
            using (var reader = new StringReader(response))
            {
                var line = reader.ReadLine();
                if (line != "HTTP/1.1 200 OK")
                {
                    return null;
                }

                int count = 0;
                while (count < SSDP_RES_MAX_READ_LINES) // Protect from evil response
                {
                    count++;
                    line = reader.ReadLine();

                    if (line == null)
                        break;
                    if (line == "")
                        continue;

                    int divider = line.IndexOf(':');
                    if (divider < 1)
                        continue;

                    string name = line.Substring(0, divider).Trim();
                    if ("location".Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Substring(divider + 1).Trim();
                    }
                }
            }

            return null;
        }

        private const string upnp_ns = "{urn:schemas-upnp-org:device-1-0}";
        private const string sony_ns = "{urn:schemas-sony-com:av}";

        public static SonyCameraDeviceInfo AnalyzeDescription(string response)
        {
            //Log(response);
            var endpoints = new Dictionary<string, string>();

            var xml = XDocument.Parse(response);
            var device = xml.Root.Element(upnp_ns + "device");
            if (device == null)
            {
                return null;
            }
            var f_name = device.Element(upnp_ns + "friendlyName").Value;
            var m_name = device.Element(upnp_ns + "modelName").Value;
            var udn = device.Element(upnp_ns + "UDN").Value;
            var info = device.Element(sony_ns + "X_ScalarWebAPI_DeviceInfo");
            if (info == null)
            {
                return null;
            }
            var list = info.Element(sony_ns + "X_ScalarWebAPI_ServiceList");

            foreach (var service in list.Elements())
            {
                var name = service.Element(sony_ns + "X_ScalarWebAPI_ServiceType").Value;
                var url = service.Element(sony_ns + "X_ScalarWebAPI_ActionList_URL").Value;
                if (name == null || url == null)
                    continue;

                string endpoint;
                if (url.EndsWith("/"))
                    endpoint = url + name;
                else
                    endpoint = url + "/" + name;

                endpoints.Add(name, endpoint);
            }

            if (endpoints.Count == 0)
            {
                return null;
            }

            return new SonyCameraDeviceInfo(udn, m_name, f_name, endpoints);
        }

        private static void Log(string message)
        {
            Debug.WriteLine("[SoDiscovery] " + message);
        }
    }

    public class DeviceDescriptionEventArgs : EventArgs
    {
        public string Description { private set; get; }

        public DeviceDescriptionEventArgs(string description, Uri location, HostName local)
        {
            Description = description;
            Location = location;
            LocalAddress = local;
        }

        public Uri Location { private set; get; }
        public HostName LocalAddress { private set; get; }
    }

    public class SonyCameraDeviceEventArgs : EventArgs
    {
        public SonyCameraDeviceInfo SonyCameraDevice { private set; get; }

        public SonyCameraDeviceEventArgs(SonyCameraDeviceInfo info, Uri location, HostName local)
        {
            SonyCameraDevice = info;
            Location = location;
            LocalAddress = local;
        }

        public Uri Location { private set; get; }
        public HostName LocalAddress { private set; get; }
    }
}
