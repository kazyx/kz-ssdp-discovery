kz-ssdp-discovery
=============
- SSDP client library specialized to find Sony camera API endpoint.
- Standard SSDP search is also supported.

##Build
1. Clone repository.
 ``` bash
 git clone git@github.com:kazyx/kz-ssdp-discovery.git
 ```

2. Open csproj file by Visual Studio
 - /Project/KzSsdpDiscoveryUwp.csproj for Universal Windows Platform application.
 - /Project/KzSsdpDiscovery.csproj for Windows Phone 8.
 - /Project/KzSsdpDiscoveryUniversal.csproj for Windows 8.1 Universal application.

##Discover sony camera API endpoint.
1. Create SsdpDiscovery instance and set Event handler.
 ``` cs
 SsdpDiscovery discovery = new SsdpDiscovery();
 discovery.SonyCameraDeviceDiscovered += (sender, e) => {
    var endpoints = e.SonyCameraDevice.Endpoints; // Dictionary of each service name and endpoint.
 };
 ```

2. Start searching.
 ``` cs
 discovery.SearchSonyCameraDevices();
 ``` 

##Discover device description of UPnP devices.
1. Create SoDiscovery instance and set Event handler.
 ``` cs
 SsdpDiscovery discovery = new SsdpDiscovery();
 discovery.DescriptionObtained += (sender, e) => {
    var description = e.Description; // Body of device description xml file.
 };
 ```

2. Start searching.
 ``` cs
 discovery.SearchUpnpDevices("ssdp:all"); // You can set any ST parameter here.
 ``` 

##License
This software is published under the [MIT License](http://opensource.org/licenses/mit-license.php).
