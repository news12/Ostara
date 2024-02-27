﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;

using BeeSchema;
using Be.Windows.Forms;
using SharpPcap;
using SharpPcap.LibPcap;
using PacketDotNet;

using System.Net.Sockets;

namespace Ostara
{
    partial class FormMain : Form
    {
        class PacketClass
        {
            public TcpPacket Tcp;
            public bool IsIncoming;
            public PacketInfo.Daemon Daemon;
            public ushort Port;

            public PacketClass(TcpPacket tcp, bool inc, PacketInfo.Daemon daemon, ushort port)
            {
                Tcp = tcp;
                IsIncoming = inc;
                Daemon = daemon;
                Port = port;
            }
        }

        LibPcapLiveDevice device;
        uint address;

        byte[] partil, partiw, partic, partia,
               partol, partow, partoc, partoa;

        Dictionary<ushort, Cryption.Client> clientCrypt;
        Dictionary<ushort, Cryption.Server> serverCrypt;

        ushort[] ports = { 0, 0, 0, 0 };

        bool paused, stop;
        Queue<PacketClass> packets;

        public FormMain()
        {
            InitializeComponent();

            Settings.I.Load();
            Ports.I.Load();
            Colours.I.Load();
            Ignores.I.Load();
            Comments.I.Load();

            tsbClearOnStart.Checked = Settings.I.ClearOnStart;
            hexView.BytesPerLine = (Settings.I.BytesPerLine + 1) * 8;
            tableLayoutPanel1.ColumnStyles[1].Width = hexView.RequiredWidth + 17;
            cbBytesPerLine.SelectedIndex = Settings.I.BytesPerLine;
            var ae = (Ostara.Controls.ToolStripRadioButtonMenuItem)autoExpandToolStripMenuItem.DropDownItems[(int)Settings.I.AutoExpand];
            ae.CheckState = CheckState.Checked;

            foreach (var device in CaptureDeviceList.Instance)
                tscbNet.Items.Add(GetFriendlyDeviceName(device));

            tscbNet.SelectedIndex = 0;

            tlvStructure.CanExpandGetter = e => ((Result)e).HasChildren;
            tlvStructure.ChildrenGetter = e => ((Result)e).Children;
            tlvStructure.Expanded += (s, e) =>
            {
                if (Settings.I.AutoExpand != Settings.AutoExpandType.Bitfields)
                    return;

                var r = (Result)e.Model;

                foreach (var v in r)
                    if (v.Type == NodeType.Bitfield)
                        tlvStructure.Expand(v);
            };

            olvValue.AspectToStringConverter = x =>
            {
                if (x is ResultCollection)
                    return string.Empty;
                else
                    return $"{x}";
            };
        }


        string GetFriendlyDeviceName(ILiveDevice d)
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var n in nics)
            {
                if (d.Name.EndsWith(n.Id))
                    return n.Name;
            }

            return d.Name;
        }

        void FormMain_FormClosing(object sender, FormClosingEventArgs e)
        {

            Ignores.I.Save();
            Settings.I.Save();
            stop = true;
        }

        void tsbOpen_Click(object sender, EventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Title = "Open packet log file...";
            dlg.Filter = "Packet Log|*.log";

            if (dlg.ShowDialog() == DialogResult.OK)
                flvPackets.SetObjects(PacketLog.Load(dlg.FileName));

            dlg.Dispose();
        }

        void tsbSave_Click(object sender, EventArgs e)
        {
            if (flvPackets.Objects == null || flvPackets.GetItemCount() == 0)
                return;

            var file = PacketLog.Save(flvPackets.Objects);
            MessageBox.Show($"Packet log saved as {file}");
        }

        void tsbClearOnStart_CheckedChanged(object sender, EventArgs e)
        {
            Settings.I.ClearOnStart = tsbClearOnStart.Checked;
        }

        void tsbClear_Click(object sender, EventArgs e)
        {
            flvPackets.ClearObjects();
            hexView.ByteProvider = null;
            ClearInspector();
        }

        void tsbColours_Click(object sender, EventArgs e)
        {
            var dlg = new FormColours();
            dlg.SetDesktopLocation(Cursor.Position.X, Cursor.Position.Y);

            if (dlg.ShowDialog() == DialogResult.Cancel)
            {
                dlg.Dispose();
                return;
            }

            Colours.I.Pairs = new Dictionary<Tuple<PacketInfo.Daemon, PacketInfo.Daemon>, Tuple<Color, Color>>(dlg.Pairs);
            Colours.I.Save();
            flvPackets.Refresh();

            dlg.Dispose();
        }

        void tsmiIgnoreAdd_Click(object sender, EventArgs e)
        {
            if (flvPackets.SelectedIndex == -1)
                return;

            var p = (PacketInfo)flvPackets.SelectedObject;

            if (!Ignores.I.Values.Contains(p.Opcode))
                Ignores.I.Values.Add(p.Opcode);
        }

        void tsmiIgnoreRemove_Click(object sender, EventArgs e)
        {
            if (flvPackets.SelectedIndex == -1)
                return;

            var p = (PacketInfo)flvPackets.SelectedObject;
            Ignores.I.Values.Remove(p.Opcode);
        }

        void tsmiIgnoreManage_Click(object sender, EventArgs e)
        {
            var dlg = new FormIgnores();
            dlg.SetDesktopLocation(Cursor.Position.X, Cursor.Position.Y);

            if (dlg.ShowDialog() == DialogResult.Cancel)
            {
                dlg.Dispose();
                return;
            }

            Ignores.I.Values = new List<ushort>(dlg.Opcodes);
            Ignores.I.Save();

            dlg.Dispose();
        }

        void tsbAbout_Click(object sender, EventArgs e)
        {
            var dlg = new FormAbout();
            dlg.ShowDialog();
            dlg.Dispose();
        }

        void tsbStart_Click(object sender, EventArgs e)
        {
            stop = false;

            tsbStart.Enabled = tscbNet.Enabled = false;
            tsbPause.Enabled = tsbStop.Enabled = true;

            this.Text = "Ostara - Logging";

            if (paused)
            {
                paused = false;
                return;
            }

            if (Settings.I.ClearOnStart)
                flvPackets.ClearObjects();

            packets = new Queue<PacketClass>();
            clientCrypt = new Dictionary<ushort, Cryption.Client>();
            serverCrypt = new Dictionary<ushort, Cryption.Server>();

            int ni = tscbNet.SelectedIndex;

            var devices = CaptureDeviceList.Instance;
            if (ni < devices.Count)
            {
                var device = devices[ni];
                device.Open(DeviceModes.Promiscuous, 1000); // size of the capture buffer

                var ipAddress = GetDeviceIPAddress(device);

                Task.Run(() => { GetPackets(device); });
            }
        }

        string GetDeviceIPAddress(ILiveDevice device)
        {
            var captureDevice = (LibPcapLiveDevice)device;

            // Itera pelos endereços e encontra o primeiro endereço IPv4
            foreach (var address in captureDevice.Addresses)
            {
                if (address.Addr.ipAddress != null && address.Addr.ipAddress.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address.Addr.ipAddress.ToString();
                }
            }

            return null;
        }

        bool checkPort(ushort port)
        {
            foreach (var p in Ports.I.LoginPorts)
            {
                if (p == port)
                    return true;
            }

            int minPort = Ports.I.WorldPortsStart[0];
            int maxPort = Ports.I.WorldPortsEnd[0];

            if (port >= minPort && port <= maxPort)
                return true;

            return false;
        }

        void GetPackets(ICaptureDevice device)
        {
            device.OnPacketArrival += (sender, e) =>
            {
                var packet = Packet.ParsePacket(e.GetPacket().LinkLayerType, e.Data.ToArray());

                //var nPacket = (IPPacket)packet.Extract<IPPacket>();

                if (packet is EthernetPacket ethernetPacket && packet.PayloadPacket is IPPacket ipPacket && ipPacket.PayloadPacket is TcpPacket tcpPacket)
                {
                    var data = tcpPacket.PayloadData;

                    if (data.Length == 0)
                    {

                        return;
                    }

                    var inc = IsIncoming(ipPacket);
                    var isl = IsLogin(tcpPacket, inc);
                    var isw = IsWorld(tcpPacket, inc);
                    var isc = IsChat(tcpPacket, inc);
                    var isa = IsAuction(tcpPacket, inc);

                    ushort port = (inc) ? tcpPacket.SourcePort : tcpPacket.DestinationPort;
                    ushort lport = (inc) ? tcpPacket.DestinationPort : tcpPacket.SourcePort;

                    if (!clientCrypt.ContainsKey(port) || ((isl && lport != ports[0]) || (isw && lport != ports[1]) || (isc && lport != ports[2] || (isa && lport != ports[3]))))
                    {
                        Console.WriteLine($"Creating new encryption instances for [{ipPacket.SourceAddress} - {ipPacket.DestinationAddress}]");
                        clientCrypt[port] = new Cryption.Client();
                        serverCrypt[port] = new Cryption.Server();

                        if (isl)
                        {
                            ports[0] = lport;
                            partil = null;
                            partol = null;
                        }
                        else if (isw)
                        {
                            ports[1] = lport;
                            partiw = null;
                            partow = null;
                        }
                        else if (isc)
                        {
                            ports[2] = lport;
                            partic = null;
                            partoc = null;
                        }
                        else
                        {
                            ports[3] = lport;
                            partia = null;
                            partoa = null;
                        }
                    }

                    var daemon =
                        (isl) ? PacketInfo.Daemon.Login :
                        (isw) ? PacketInfo.Daemon.World :
                        (isc) ? PacketInfo.Daemon.Chat :
                        PacketInfo.Daemon.Auction;

                    AddPacket(tcpPacket, inc, daemon, port);
                }
            };

            device.Open(DeviceModes.Promiscuous, 1000);
            device.StartCapture();
        }



        void AddPacket(TcpPacket tcp, bool inc, PacketInfo.Daemon daemon, ushort port)
        {
            // Verifica se o objeto tcp é nulo
            if (tcp == null)
            {
#if DEBUG
                Console.WriteLine("Dropping null tcp packet...");
#endif
                return;
            }

            foreach (var p in packets)
            {
                // Verifica se o objeto tcp na lista é nulo
                if (p != null && p.Tcp != null && p.Tcp.SequenceNumber == tcp.SequenceNumber)
                {
#if DEBUG
                    Console.WriteLine("Dropping duplicate packet...");
#endif
                    return;
                }
            }

            // Verifica se o objeto tcp não é nulo antes de adicionar à fila
            if (tcp != null)
            {
                packets.Enqueue(new PacketClass(tcp, inc, daemon, port));

                if (packets.Count > 10)
                {
                    var p = packets.Dequeue();

                    // Verifica se o objeto tcp em p não é nulo antes de processar
                    if (p != null && p.Tcp != null)
                    {
                        var data = p.Tcp.PayloadData;
                        ProcessPacket(data.ToArray(), p.IsIncoming, p.Daemon, p.Port);
                    }
                }
            }
        }


        void DumpPackets()
        {
            while (packets.Count > 0)
            {
                var p = packets.Dequeue();
                var data = p.Tcp.PayloadData;
                ProcessPacket(data.ToArray(), p.IsIncoming, p.Daemon, p.Port);
            }
        }

        void ProcessPacket(byte[] data, bool inc, PacketInfo.Daemon daemon, ushort port)
        {
            int s;
            byte[] tmp;

            if (inc)
            {
                switch (daemon)
                {
                    case PacketInfo.Daemon.Login:
                        if (partil == null)
                            break;

                        s = partil.Length + data.Length;
                        tmp = new byte[s];
                        Array.ConstrainedCopy(partil, 0, tmp, 0, partil.Length);
                        Array.ConstrainedCopy(data, 0, tmp, partil.Length, data.Length);
                        data = tmp;
                        partil = null;
                        break;
                    case PacketInfo.Daemon.World:
                        if (partiw == null)
                            break;

                        s = partiw.Length + data.Length;
                        tmp = new byte[s];
                        Array.ConstrainedCopy(partiw, 0, tmp, 0, partiw.Length);
                        Array.ConstrainedCopy(data, 0, tmp, partiw.Length, data.Length);
                        data = tmp;
                        partiw = null;
                        break;
                    case PacketInfo.Daemon.Chat:
                        if (partic == null)
                            break;

                        s = partic.Length + data.Length;
                        tmp = new byte[s];
                        Array.ConstrainedCopy(partic, 0, tmp, 0, partic.Length);
                        Array.ConstrainedCopy(data, 0, tmp, partic.Length, data.Length);
                        data = tmp;
                        partic = null;
                        break;
                    case PacketInfo.Daemon.Auction:
                        if (partia == null)
                            break;

                        s = partia.Length + data.Length;
                        tmp = new byte[s];
                        Array.ConstrainedCopy(partia, 0, tmp, 0, partia.Length);
                        Array.ConstrainedCopy(data, 0, tmp, partia.Length, data.Length);
                        data = tmp;
                        partia = null;
                        break;
                }
            }
            else
            {
                switch (daemon)
                {
                    case PacketInfo.Daemon.Login:
                        if (partol == null)
                            break;

                        s = partol.Length + data.Length;
                        tmp = new byte[s];
                        Array.ConstrainedCopy(partol, 0, tmp, 0, partol.Length);
                        Array.ConstrainedCopy(data, 0, tmp, partol.Length, data.Length);
                        data = tmp;
                        partol = null;
                        break;
                    case PacketInfo.Daemon.World:
                        if (partow == null)
                            break;

                        s = partow.Length + data.Length;
                        tmp = new byte[s];
                        Array.ConstrainedCopy(partow, 0, tmp, 0, partow.Length);
                        Array.ConstrainedCopy(data, 0, tmp, partow.Length, data.Length);
                        data = tmp;
                        partow = null;
                        break;
                    case PacketInfo.Daemon.Chat:
                        if (partoc == null)
                            break;

                        s = partoc.Length + data.Length;
                        tmp = new byte[s];
                        Array.ConstrainedCopy(partoc, 0, tmp, 0, partoc.Length);
                        Array.ConstrainedCopy(data, 0, tmp, partoc.Length, data.Length);
                        data = tmp;
                        partoc = null;
                        break;
                    case PacketInfo.Daemon.Auction:
                        if (partoa == null)
                            break;

                        s = partoa.Length + data.Length;
                        tmp = new byte[s];
                        Array.ConstrainedCopy(partoa, 0, tmp, 0, partoa.Length);
                        Array.ConstrainedCopy(data, 0, tmp, partoa.Length, data.Length);
                        data = tmp;
                        partoa = null;
                        break;
                }
            }

            var size = (inc)
                ? serverCrypt[port].GetPacketSize(data)
                : clientCrypt[port].GetPacketSize(data);

            if (size > data.Length)
            {
#if DEBUG
                //Console.WriteLine("Found partial packet. Storing...");
#endif

                if (inc)
                {
                    if (daemon == PacketInfo.Daemon.Login)
                    {
                        partil = new byte[data.Length];
                        Array.ConstrainedCopy(data, 0, partil, 0, data.Length);
                    }
                    else if (daemon == PacketInfo.Daemon.World)
                    {
                        partiw = new byte[data.Length];
                        Array.ConstrainedCopy(data, 0, partiw, 0, data.Length);
                    }
                    else if (daemon == PacketInfo.Daemon.Chat)
                    {
                        partic = new byte[data.Length];
                        Array.ConstrainedCopy(data, 0, partic, 0, data.Length);
                    }
                    else if (daemon == PacketInfo.Daemon.Auction)
                    {
                        partia = new byte[data.Length];
                        Array.ConstrainedCopy(data, 0, partia, 0, data.Length);
                    }
                }
                else
                {
                    if (daemon == PacketInfo.Daemon.Login)
                    {
                        partol = new byte[data.Length];
                        Array.ConstrainedCopy(data, 0, partol, 0, data.Length);
                    }
                    else if (daemon == PacketInfo.Daemon.World)
                    {
                        partow = new byte[data.Length];
                        Array.ConstrainedCopy(data, 0, partow, 0, data.Length);
                    }
                    else if (daemon == PacketInfo.Daemon.Chat)
                    {
                        partoc = new byte[data.Length];
                        Array.ConstrainedCopy(data, 0, partoc, 0, data.Length);
                    }
                    else if (daemon == PacketInfo.Daemon.Auction)
                    {
                        partoa = new byte[data.Length];
                        Array.ConstrainedCopy(data, 0, partoa, 0, data.Length);
                    }
                }

                return;
            }

            if (size < data.Length)
            {
                // Process the first subpacket
                var sub = new byte[size];
                Array.ConstrainedCopy(data, 0, sub, 0, size);
                ProcessPacket(sub, inc, daemon, port);

                // Process the rest of the data.  May contain further subpackets, or a partial packet.  We don't care
                sub = new byte[data.Length - size];
                Array.ConstrainedCopy(data, size, sub, 0, data.Length - size);
                ProcessPacket(sub, inc, daemon, port);

                return;
            }

            if (inc)
                serverCrypt[port].Decrypt(data);
            else
                clientCrypt[port].Decrypt(data);

            var ppkt = new PacketInfo(data, DateTime.Now, inc, daemon);

            if (inc && ((ppkt.Opcode == 101 && daemon == PacketInfo.Daemon.Login) ||
                        (ppkt.Opcode == 140 && daemon == PacketInfo.Daemon.World) ||
                        (ppkt.Opcode == 401 && daemon == PacketInfo.Daemon.Chat) ||
                        (ppkt.Opcode == 101 && daemon == PacketInfo.Daemon.Auction)))
            {
                unsafe
                {
                    fixed (byte* pp = data)
                    {
                        var key = *(uint*)&pp[6];
                        var step = *(ushort*)&pp[16];

                        serverCrypt[port].ChangeKey(key, step);
                        clientCrypt[port].ChangeKey(key, step);
                    }
                }
            }

            if (!paused)
            {
                if (flvPackets.InvokeRequired)
                    this.Invoke(new AddPacketDel(AddPacket), new object[] { ppkt });
                else
                    AddPacket(ppkt);
            }
        }

        void tsbPause_Click(object sender, EventArgs e)
        {
            paused = true;
            tsbPause.Enabled = false;
            tsbStart.Enabled = true;

            this.Text = "Ostara - Paused";
        }

        void tsbStop_Click(object sender, EventArgs e)
        {
            stop = true;
            paused = false;

            device = null;


            tsbStart.Enabled = tscbNet.Enabled = true;
            tsbPause.Enabled = tsbStop.Enabled = false;

            this.Text = "Ostara - Stopped";
        }

        void flvPackets_CellRightClick(object sender, BrightIdeasSoftware.CellRightClickEventArgs e)
        {
            if (e.RowIndex == -1)
                return;

            EditStructure();
        }

        void flvPackets_FormatRow(object sender, BrightIdeasSoftware.FormatRowEventArgs e)
        {
            var p = (PacketInfo)e.Model;
            Colours.I.FormatRow(e.Item, p.Source, p.Destination);

            e.Item.ToolTipText = p.Comment;
        }

        void flvPackets_ItemsChanged(object sender, BrightIdeasSoftware.ItemsChangedEventArgs e)
        {
            if (flvPackets.GetItemCount() == 0)
                return;

            var i = (flvPackets.SelectedIndex != -1) ? flvPackets.SelectedIndex : flvPackets.GetItemCount() - 1;
            flvPackets.EnsureVisible(i);
        }

        void flvPackets_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar != (char)27)
                return;

            flvPackets.SelectedIndex = -1;
            e.Handled = true;
        }

        void flvPackets_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (flvPackets.SelectedIndex < 0)
            {
                ClearInspector();
                hexView.ByteProvider = null;
                tlvStructure.ClearObjects();
            }
            else
            {
                var pi = (PacketInfo)flvPackets.SelectedObject;
                var bytes = new DynamicByteProvider(pi.Data);
                hexView.ByteProvider = bytes;
                SetStructureObjects(ParsePacketData(pi));
                tlvStructure.AutoResizeColumn(0, ColumnHeaderAutoResizeStyle.ColumnContent);
            }
        }

        ResultCollection ParsePacketData(PacketInfo packet)
        {
            var filename = $"cfg\\structs\\{GetSchemaFilename(packet)}";

            if (!File.Exists(filename))
                return null;

            ResultCollection r = null;

            try
            {
                r = Schema.FromFile(filename).Parse(packet.Data);
            }
            catch (Exception e)
            {
#if DEBUG
                Console.WriteLine(e.Message);
#else
				MessageBox.Show(e.Message);
#endif
            }

            return r;
        }

        string GetSchemaFilename(PacketInfo packet)
        {
            PacketInfo.Daemon daemon;
            var opcode = packet.Opcode;

            if (packet.IsIncoming)
                daemon = packet.Source;
            else
                daemon = packet.Destination;

            var dchar =
                (daemon == PacketInfo.Daemon.Auction) ? 'a' :
                (daemon == PacketInfo.Daemon.Chat) ? 'c' :
                (daemon == PacketInfo.Daemon.Login) ? 'l' :
                'w';

            var ichar = (packet.IsIncoming) ? 'i' : 'o';

            return $"{opcode}.{ichar}.{dchar}.bee";
        }

        void hexView_Copied(object sender, EventArgs e)
        {
            hexView.CopyHex();
        }

        unsafe void hexView_SelectionStartChanged(object sender, EventArgs e)
        {
            int s = flvPackets.SelectedIndex;
            int i = (int)hexView.SelectionStart;

            if (s < 0 || i < 0)
            {
                ClearInspector();
                return;
            }

            fixed (byte* p = ((PacketInfo)flvPackets.SelectedObject).Data)
            {
                inspByte.Text = p[i].ToString();
                inspSByte.Text = ((sbyte)p[i]).ToString();
                inspShort.Text = (*(short*)&p[i]).ToString();
                inspUShort.Text = (*(ushort*)&p[i]).ToString();
                inspInt.Text = (*(int*)&p[i]).ToString();
                inspUInt.Text = (*(uint*)&p[i]).ToString();
                inspLong.Text = (*(long*)&p[i]).ToString();
                inspULong.Text = (*(ulong*)&p[i]).ToString();
                inspFloat.Text = (*(float*)&p[i]).ToString();
                inspDouble.Text = (*(double*)&p[i]).ToString();
            }
        }

        void cbBytesPerLine_SelectedIndexChanged(object sender, EventArgs e)
        {
            Settings.I.BytesPerLine = (byte)cbBytesPerLine.SelectedIndex;
            hexView.BytesPerLine = (Settings.I.BytesPerLine + 1) * 8;
            tableLayoutPanel1.ColumnStyles[1].Width = hexView.RequiredWidth + 17;
        }

        void tlvStructure_DoubleClick(object sender, EventArgs e)
        {
            EditStructure();
        }

        void tlvStructure_Collapsed(object sender, BrightIdeasSoftware.TreeBranchCollapsedEventArgs e)
        {
            tlvStructure.AutoResizeColumn(0, ColumnHeaderAutoResizeStyle.ColumnContent);
        }

        void tlvStructure_Expanded(object sender, BrightIdeasSoftware.TreeBranchExpandedEventArgs e)
        {
            tlvStructure.AutoResizeColumn(0, ColumnHeaderAutoResizeStyle.ColumnContent);
        }

        void tlvStructure_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tlvStructure.SelectedIndex == -1)
                hexView.Select(0, 0);
            else
            {
                var se = (Result)tlvStructure.SelectedObject;
                hexView.Select(se.Position, se.Size);
            }
        }

        void cmseStructureEdit_Click(object sender, EventArgs e)
        {
            EditStructure();
        }

        void AddPacket(PacketInfo p)
        {
            if (!Ignores.I.Values.Contains(p.Opcode))
                flvPackets.AddObject(p);
        }

        void autoExpandCheckStateChanged(object sender, EventArgs e)
        {
            if (noneToolStripMenuItem.CheckState == CheckState.Checked)
                Settings.I.AutoExpand = Settings.AutoExpandType.None;
            else if (bitfieldsOnlyToolStripMenuItem.CheckState == CheckState.Checked)
                Settings.I.AutoExpand = Settings.AutoExpandType.Bitfields;
            else if (allToolStripMenuItem.CheckState == CheckState.Checked)
                Settings.I.AutoExpand = Settings.AutoExpandType.All;
        }

        delegate void AddPacketDel(PacketInfo p);

        void ClearInspector()
        {
            inspByte.Text = inspSByte.Text = inspShort.Text = inspUShort.Text =
            inspInt.Text = inspUInt.Text = inspLong.Text = inspULong.Text =
            inspFloat.Text = inspDouble.Text = "";
        }

        bool IsIncoming(IPPacket ip)
            => address == ((uint)ip.DestinationAddress.AddressFamily);

        bool IsLogin(TcpPacket tcp, bool inc)
            => Ports.I.LoginPorts.Contains(inc ? tcp.SourcePort : tcp.DestinationPort);

        bool IsWorld(TcpPacket tcp, bool inc)
        {
            for (int i = 0; i < Ports.I.WorldPortsStart.Count; i++)
            {
                if (inc)
                {
                    if (tcp.SourcePort >= Ports.I.WorldPortsStart[i] && tcp.SourcePort <= Ports.I.WorldPortsEnd[i])
                        return true;
                }
                else
                {
                    if (tcp.DestinationPort >= Ports.I.WorldPortsStart[i] && tcp.DestinationPort <= Ports.I.WorldPortsEnd[i])
                        return true;
                }
            }

            return false;
        }

        bool IsChat(TcpPacket tcp, bool inc)
            => Ports.I.ChatPorts.Contains(inc ? tcp.SourcePort : tcp.DestinationPort);

        bool IsAuction(TcpPacket tcp, bool inc)
            => Ports.I.AuctionPorts.Contains(inc ? tcp.SourcePort : tcp.DestinationPort);

        void EditStructure()
        {
            var packet = (PacketInfo)flvPackets.SelectedObject;
            var filename = GetSchemaFilename(packet);
            var path = $"{Environment.CurrentDirectory}\\cfg\\structs\\";

            if (!File.Exists(path + filename))
            {
                var file = File.CreateText(path + filename);
                file.WriteLine("include header.bee;");
                file.WriteLine();
                file.WriteLine("schema {");
                file.Write("\theader:\t\t");

                if (filename.Contains(".i."))
                    file.WriteLine("ServerHeader;");
                else
                    file.WriteLine("ClientHeader;");

                file.WriteLine("\t");
                file.WriteLine("}");
                file.Close();
            }

            var watcher = new FileSystemWatcher(path, filename);
            watcher.NotifyFilter = NotifyFilters.LastWrite;
            watcher.EnableRaisingEvents = true;
            watcher.Changed += async (s, e) =>
            {
                await Task.Delay(500);
                Action a = () => SetStructureObjects(ParsePacketData(packet));
                tlvStructure.Invoke(a);
                a = () => tlvStructure.AutoResizeColumn(0, ColumnHeaderAutoResizeStyle.ColumnContent);
                tlvStructure.Invoke(a);
            };

            var p = new Process();
            p.EnableRaisingEvents = true;
            p.Exited += (s, e) =>
            {
                watcher.Dispose();
            };
            var pi = new ProcessStartInfo(path + filename);
            p.StartInfo = pi;
            p.Start();
        }

        void SetStructureObjects(ResultCollection collection)
        {
            tlvStructure.SetObjects(collection);

            switch (Settings.I.AutoExpand)
            {
                case Settings.AutoExpandType.None:
                    break;
                case Settings.AutoExpandType.Bitfields:
                    foreach (var v in collection)
                        if (v.Type == NodeType.Bitfield)
                            tlvStructure.Expand(v);
                    break;
                case Settings.AutoExpandType.All:
                    tlvStructure.ExpandAll();
                    break;
            }
        }
    }
}