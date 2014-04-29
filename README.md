KzSoDiscovery
=============
- SSDP client library specialized to find Sony camera API endpoint.
- Standard SSDP search is also supported.

##Build
1. Clone repository.
 ``` bash
 git clone git@github.com:kazyx/KzSoDiscovery.git
 ```

2. Open csproj file by Visual Studio
 - /Project/KzSoDiscovery.csproj for Windows Phone 8.
 - /Project/KzSoDiscoveryUniversal.csproj for Universal Windows application.

##Discover sony camera API endpoint.
1. Create SoDiscovery instance and set Event handler.
 ``` cs
 SoDiscovery discovery = new SoDiscovery();
 discovery.ScalarDeviceDiscovered += (sender, e) => {
    var endpoints = e.ScalarDevice.Endpoints; // Dictionary of each service name and endpoint.
 };
 ```

2. Start searching.
 ``` cs
 discovery.SearchScalarDevices();
 ``` 

##Discover device description of UPnP devices.
1. Create SoDiscovery instance and set Event handler.
 ``` cs
 SoDiscovery discovery = new SoDiscovery();
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
