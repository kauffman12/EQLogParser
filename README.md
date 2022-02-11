# EQLogParser
Everquest Log Parser for Live? servers. 

The software depends on a Docking UI control from Actipro so needs a license to build
and run without having an Actipro splash screen. One day I may find a good replacement
so that it's all open source. I've only tested on Live but it may work fine on TLP.
I imagine it won't work at all for P99.

The Releases folder has recent builds. The msi installer and exe have now been signed with a real cert. It still will display Unknown Publisher messages in windows but if you do the following you can avoid those in the future.

1. right-click the msi file and choose properties
2. under the digital signatures tab select the one signature and click details
3. click View Certificate
4. click Install Certificate

# Example
![Parser](./examples/example1.png)

# Overlay Example
![Overlay](./examples/example2.png)

