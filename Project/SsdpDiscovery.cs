using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
#if WINDOWS_PHONE
using System.Net.Sockets;
#elif WINDOWS_PHONE_APP||WINDOWS_APP||NETFX_CORE
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Networking.Sockets;
using Windows.Networking;
using Windows.Foundation;
using Windows.Networking.Connectivity;
using Windows.Web.Http;
#endif

namespace Kazyx.DeviceDiscovery
{
    public class SsdpDiscovery
    {
        private const string MULTICAST_ADDRESS = "239.255.255.250";
        private const int SSDP_PORT = 1900;
        private const int RESULT_BUFFER = 8192;

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

            int timeoutSec = (timeout == null) ? (int)DEFAULT_TIMEOUT.TotalSeconds : (int)timeout.Value.TotalSeconds;

            if (timeoutSec < 2)
            {
                timeoutSec = 2;
            }

            var ssdp_data = new StringBuilder()
                .Append("M-SEARCH * HTTP/1.1").Append("\r\n")
                .Append("HOST: ").Append(MULTICAST_ADDRESS).Append(":").Append(SSDP_PORT.ToString()).Append("\r\n")
                .Append("MAN: ").Append("\"ssdp:discover\"").Append("\r\n")
                .Append("MX: ").Append(MX.ToString()).Append("\r\n")
                .Append("ST: ").Append(st).Append("\r\n")
                .Append("\r\n")
                .ToString();
            var data_byte = Encoding.UTF8.GetBytes(ssdp_data);

            var timeout_called = false;

#if WINDOWS_PHONE
            var DD_Handler = new AsyncCallback(ar =>
            {
                if (timeout_called)
                {
                    return;
                }

                var req = ar.AsyncState as HttpWebRequest;

                try
                {
                    var res = req.EndGetResponse(ar) as HttpWebResponse;
                    using (var reader = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    {
                        try
                        {
                            var response = reader.ReadToEnd();
                            OnDiscovered(new DeviceDescriptionEventArgs(response));
                            OnDiscovered(new SonyCameraDeviceEventArgs(AnalyzeDescription(response)));
                        }
                        catch (Exception)
                        {
                            Log("Invalid XML");
                            //Invalid XML.
                        }
                    }
                }
                catch (WebException)
                {
                    //Invalid DD location or network error.
                }
            });
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SendBufferSize = data_byte.Length;

            var snd_event_args = new SocketAsyncEventArgs();
            snd_event_args.RemoteEndPoint = new IPEndPoint(IPAddress.Parse(MULTICAST_ADDRESS), SSDP_PORT);
            snd_event_args.SetBuffer(data_byte, 0, data_byte.Length);

            var rcv_event_args = new SocketAsyncEventArgs();
            rcv_event_args.SetBuffer(new byte[RESULT_BUFFER], 0, RESULT_BUFFER);

            var SND_Handler = new EventHandler<SocketAsyncEventArgs>((sender, e) =>
            {
                if (e.SocketError == SocketError.Success && e.LastOperation == SocketAsyncOperation.SendTo)
                {
                    try
                    {
                        socket.ReceiveBufferSize = RESULT_BUFFER;
                        socket.ReceiveAsync(rcv_event_args);
                    }
                    catch (ObjectDisposedException)
                    {
                        Log("Socket is already disposed.");
                    }
                }
            });
            snd_event_args.Completed += SND_Handler;

            var RCV_Handler = new EventHandler<SocketAsyncEventArgs>((sender, e) =>
            {
                if (e.SocketError == SocketError.Success && e.LastOperation == SocketAsyncOperation.Receive)
                {
                    string result = Encoding.UTF8.GetString(e.Buffer, 0, e.BytesTransferred);
                    //Log(result);

                    GetDeviceDescriptionAsync(DD_Handler, result);

                    try
                    {
                        socket.ReceiveAsync(e);
                    }
                    catch (ObjectDisposedException)
                    {
                        Log("Socket is already disposed.");
                    }
                }
            });
            rcv_event_args.Completed += RCV_Handler;
            socket.SendToAsync(snd_event_args);
#elif WINDOWS_PHONE_APP||WINDOWS_APP||NETFX_CORE
            var handler = new TypedEventHandler<DatagramSocket, DatagramSocketMessageReceivedEventArgs>(async (sender, args) =>
            {
                if (timeout_called || args == null)
                {
                    return;
                }
                var reader = args.GetDataReader();
                var data = reader.ReadString(reader.UnconsumedBufferLength);
                Log(data);
                await GetDeviceDescriptionAsync(data, args.LocalAddress);
            });

            var filter = new ConnectionProfileFilter
            {
                IsConnected = true,
                IsWwanConnectionProfile = false,
                IsWlanConnectionProfile = true
            };
            var profiles = await NetworkInformation.FindConnectionProfilesAsync(filter);
            var sockets = new List<DatagramSocket>();
            foreach (var profile in profiles)
            {
                var socket = new DatagramSocket();
                sockets.Add(socket);
                socket.MessageReceived += handler;
                try
                {
                    Log("Send M-Search to " + profile.ProfileName);
                    await socket.BindServiceNameAsync("", profile.NetworkAdapter);
                }
                catch (Exception)
                {
                    Log("Failed to bind NetworkAdapter");
                    return;
                }

                var host = new HostName(MULTICAST_ADDRESS);
                try
                {
                    var output = await socket.GetOutputStreamAsync(host, SSDP_PORT.ToString());
                    await output.WriteAsync(data_byte.AsBuffer());
                    await socket.OutputStream.FlushAsync();
                }
                catch (Exception)
                {
                    Log("Failed to send multicast");
                    return;
                }
            }
#endif

            await Task.Delay(TimeSpan.FromSeconds(timeoutSec));

            Log("Search Timeout");
            timeout_called = true;
#if WINDOWS_PHONE
            snd_event_args.Completed -= SND_Handler;
            rcv_event_args.Completed -= RCV_Handler;
            socket.Close();
#elif WINDOWS_PHONE_APP||WINDOWS_APP||NETFX_CORE
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
#endif
            OnTimeout(new EventArgs());
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

#if WINDOWS_PHONE
        private static void GetDeviceDescriptionAsync(AsyncCallback ac, string data, HostName host)
        {
            var dd_location = ParseLocation(data);
            if (dd_location != null)
            {
                try
                {
                    var req = HttpWebRequest.Create(new Uri(dd_location)) as HttpWebRequest;
                    req.Method = "GET";
                    req.BeginGetResponse(ac, req);
                }
                catch (Exception)
                {
                    //Invalid DD location.
                }
            }
        }
#elif WINDOWS_PHONE_APP||WINDOWS_APP||NETFX_CORE
        private HttpClient HttpClient = new HttpClient();

        private async Task GetDeviceDescriptionAsync(string data, HostName remoteAddress)
        {
            var dd_location = ParseLocation(data);
            if (dd_location != null)
            {
                try
                {
                    var uri = new Uri(dd_location);
                    var res = await HttpClient.GetAsync(uri);
                    if (res.IsSuccessStatusCode)
                    {
                        var response = await res.Content.ReadAsStringAsync();
                        OnDiscovered(new DeviceDescriptionEventArgs(response, uri, remoteAddress));
                        OnDiscovered(new SonyCameraDeviceEventArgs(AnalyzeDescription(response), uri, remoteAddress));
                    }
                }
                catch (Exception)
                {
                    //Invalid DD location.
                }
            }
        }
#endif

        private static string ParseLocation(string response)
        {
            var reader = new StringReader(response);
            var line = reader.ReadLine();
            if (line != "HTTP/1.1 200 OK")
            {
                return null;
            }

            while (true)
            {
                line = reader.ReadLine();
                if (line == null)
                    break;
                if (line == "")
                    continue;

                int divider = line.IndexOf(':');
                if (divider < 1)
                    continue;

                string name = line.Substring(0, divider).Trim();
                if (name == "LOCATION" || name == "location")
                {
                    return line.Substring(divider + 1).Trim();
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
            var f_name = device.Element(upnp_ns + "friendlyName").Value;
            var m_name = device.Element(upnp_ns + "modelName").Value;
            var udn = device.Element(upnp_ns + "UDN").Value;
            var info = device.Element(sony_ns + "X_ScalarWebAPI_DeviceInfo");
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
                throw new XmlException("No endoint found in XML");
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

#if WINDOWS_PHONE
        public DeviceDescriptionEventArgs(string description)
        {
            Description = description;
        }
#elif WINDOWS_PHONE_APP||WINDOWS_APP||NETFX_CORE

        public DeviceDescriptionEventArgs(string description, Uri location, HostName local)
        {
            Description = description;
            Location = location;
            LocalAddress = local;
        }

        public Uri Location { private set; get; }
        public HostName LocalAddress { private set; get; }
#endif
    }

    public class SonyCameraDeviceEventArgs : EventArgs
    {
        public SonyCameraDeviceInfo SonyCameraDevice { private set; get; }

#if WINDOWS_PHONE
        public SonyCameraDeviceEventArgs(SonyCameraDeviceInfo info)
        {
            SonyCameraDevice = info;
        }
#elif WINDOWS_PHONE_APP||WINDOWS_APP||NETFX_CORE

        public SonyCameraDeviceEventArgs(SonyCameraDeviceInfo info, Uri location, HostName local)
        {
            SonyCameraDevice = info;
            Location = location;
            LocalAddress = local;
        }

        public Uri Location { private set; get; }
        public HostName LocalAddress { private set; get; }
#endif
    }
}
