# EQLogParser
Everquest Log Parser for Live/TLP servers with basic support for P99.

Minimum Requirements:
1. Windows 10 x64
2. .Net 6.0 Desktop Runtime for x64

.Net 6.0 is provided by Microsoft but is not included with Windows. It can be downloaded from here:
https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-6.0.7-windows-x64-installer

Latest Release of EQLogParser:
https://github.com/kauffman12/EQLogParser/raw/master/Release/EQLogParser-2.0.0.msi

Note for Developers:
Syncfusion components used by this application require a license. If you apply for a community license you should be able to get one for free.

The msi installer for EQLogParser has been signed with a certificate. It's recommended that the following steps are done so that you're sure you have an official version.

Once your system trusts the certificate you'll notice the install prompt will be blue in color and no longer say Unknown Publisher. Then in the future if it starts to say Unknown Publisher you'll know that the install either wasn't from me or I had to change certificates. Which I will mention here if I have to.

1. right-click the msi file and choose properties
2. under the digital signatures tab select the one signature and click details
3. click View Certificate
4. click Install Certificate

# Example
![Parser](./examples/example1.png)

# Overlay Example
![Overlay](./examples/example2.png)

