using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Threading.Tasks;
#if WINDOWS_PHONE
using System.Net.Sockets;
#elif NETFX_CORE
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Networking.Sockets;
using Windows.Networking;
using Windows.Foundation;
using Windows.Networking.Connectivity;
#endif

namespace Kazyx.DeviceDiscovery
{
    public class SoDiscovery
    {
        private const string MULTICAST_ADDRESS = "239.255.255.250";
        private const int SSDP_PORT = 1900;
        private const int RESULT_BUFFER = 8192;

        private readonly TimeSpan DEFAULT_TIMEOUT = new TimeSpan(0, 0, 5);

        public delegate void ScalarDeviceHandler(object sender, ScalarDeviceEventArgs e);

        public event ScalarDeviceHandler ScalarDeviceDiscovered;

        protected void OnDiscovered(ScalarDeviceEventArgs e)
        {
            if (ScalarDeviceDiscovered != null)
            {
                ScalarDeviceDiscovered(this, e);
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

        private async Task Search(string st, TimeSpan? timeout = null)
        {
            Debug.WriteLine("SoDiscovery.SearchDevices");

            int timeoutSec = (timeout == null) ? (int)DEFAULT_TIMEOUT.TotalSeconds : (int)timeout.Value.TotalSeconds;

            if (timeoutSec < 2)
            {
                timeoutSec = 2;
            }

            const int MX = 1;

            var ssdp_data = new StringBuilder()
                .Append("M-SEARCH * HTTP/1.1").Append("\r\n")
                .Append("HOST: ").Append(MULTICAST_ADDRESS).Append(":").Append(SSDP_PORT.ToString()).Append("\r\n")
                .Append("MAN: ").Append("\"ssdp:discover\"").Append("\r\n")
                .Append("MX: ").Append(MX.ToString()).Append("\r\n")
                .Append("ST: ").Append(st).Append("\r\n")
                //.Append("ST: ssdp:all").Append("\r\n") // For debug
                .Append("\r\n")
                .ToString();
            byte[] data_byte = Encoding.UTF8.GetBytes(ssdp_data);
            //Debug.WriteLine(ssdp_data);

            bool timeout_called = false;

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
                            OnDiscovered(new ScalarDeviceEventArgs(AnalyzeDD(response)));
                        }
                        catch (Exception)
                        {
                            Debug.WriteLine("Invalid XML");
                            //Invalid XML.
                        }
                    }
                }
                catch (WebException)
                {
                    //Invalid DD location or network error.
                }
            });

#if WINDOWS_PHONE
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SendBufferSize = data_byte.Length;

            SocketAsyncEventArgs snd_event_args = new SocketAsyncEventArgs();
            snd_event_args.RemoteEndPoint = new IPEndPoint(IPAddress.Parse(MULTICAST_ADDRESS), SSDP_PORT);
            snd_event_args.SetBuffer(data_byte, 0, data_byte.Length);

            SocketAsyncEventArgs rcv_event_args = new SocketAsyncEventArgs();
            rcv_event_args.SetBuffer(new byte[RESULT_BUFFER], 0, RESULT_BUFFER);

            var SND_Handler = new EventHandler<SocketAsyncEventArgs>((sender, e) =>
            {
                if (e.SocketError == SocketError.Success && e.LastOperation == SocketAsyncOperation.SendTo)
                {
                    socket.ReceiveBufferSize = RESULT_BUFFER;
                    socket.ReceiveAsync(rcv_event_args);
                }
            });
            snd_event_args.Completed += SND_Handler;

            var RCV_Handler = new EventHandler<SocketAsyncEventArgs>((sender, e) =>
            {
                if (e.SocketError == SocketError.Success && e.LastOperation == SocketAsyncOperation.Receive)
                {
                    string result = Encoding.UTF8.GetString(e.Buffer, 0, e.BytesTransferred);
                    //Debug.WriteLine(result);

                    GetDeviceDescriptionAsync(DD_Handler, result);

                    socket.ReceiveAsync(e);
                }
            });
            rcv_event_args.Completed += RCV_Handler;
            socket.SendToAsync(snd_event_args);
#elif NETFX_CORE
            var sock = new DatagramSocket();
            var handler = new TypedEventHandler<DatagramSocket, DatagramSocketMessageReceivedEventArgs>((sender, args) =>
            {
                if (timeout_called || args == null)
                {
                    return;
                }
                var reader = args.GetDataReader();
                string data = reader.ReadString(reader.UnconsumedBufferLength);
                Debug.WriteLine(data);

                GetDDAsync(DD_Handler, data);
            });
            sock.MessageReceived += handler;

            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null)
            {
                return;
            }

            try
            {
                await sock.BindServiceNameAsync("", profile.NetworkAdapter);
            }
            catch (Exception)
            {
                Debug.WriteLine("Failed to bind NetworkAdapter");
                return;
            }

            var host = new HostName(multicast_address);
            try
            {
                var output = await sock.GetOutputStreamAsync(host, ssdp_port.ToString());
                await output.WriteAsync(data_byte.AsBuffer());
                await sock.OutputStream.FlushAsync();
            }
            catch (Exception)
            {
                Debug.WriteLine("Failed to send multicast");
                return;
            }
#endif

            await RunTimeoutInvokerAsync(timeoutSec, () =>
            {
                Debug.WriteLine("SSDP Timeout");
                timeout_called = true;
#if WINDOWS_PHONE
                snd_event_args.Completed -= SND_Handler;
                rcv_event_args.Completed -= RCV_Handler;
                socket.Close();
#elif NETFX_CORE
                sock.Dispose();
#endif
                OnTimeout(new EventArgs());
            });
        }

        /// <summary>
        /// Search sony camera devices and retrieve the endpoint URLs.
        /// </summary>
        /// <param name="timeout">Timeout to end up search.</param>
        public async void SearchScalarDevices(TimeSpan? timeout = null)
        {
            await Search("urn:schemas-sony-com:service:ScalarWebAPI:1", timeout);
        }

        /// <summary>
        /// Search UPnP devices and retrieve the device description.
        /// </summary>
        /// <param name="st">Search Target parameter for SSDP.</param>
        /// <param name="timeout">Timeout to end up search.</param>
        public async void SearchUpnpDevices(string st, TimeSpan? timeout = null)
        {
            await Search(st, timeout);
        }

        private async Task RunTimeoutInvokerAsync(int TimeoutSec, Action OnTimeout)
        {
            await Task.Delay(TimeSpan.FromSeconds(TimeoutSec));
            OnTimeout.Invoke();
        }

        private static void GetDeviceDescriptionAsync(AsyncCallback ac, string data)
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

        private static ScalarDeviceInfo AnalyzeDD(string response)
        {
            //Debug.WriteLine(response);
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

            return new ScalarDeviceInfo(udn, m_name, f_name, endpoints);
        }
    }

    public class DeviceDescriptionEventArgs : EventArgs
    {
        private readonly string description;

        public DeviceDescriptionEventArgs(string description)
        {
            this.description = description;
        }

        public string Description
        {
            get { return description; }
        }
    }

    public class ScalarDeviceEventArgs : EventArgs
    {
        private readonly ScalarDeviceInfo info;

        public ScalarDeviceEventArgs(ScalarDeviceInfo info)
        {
            this.info = info;
        }

        public ScalarDeviceInfo ScalarDevice
        {
            get { return info; }
        }
    }
}
