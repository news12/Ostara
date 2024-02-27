﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ostara {
	static class PacketLog {
		public static List<PacketInfo> Load(string file) {
			var result = new List<PacketInfo>();

			var f = File.OpenRead(file);
			var r = new BinaryReader(f, Encoding.ASCII);

			while (r.PeekChar() != -1) {
				var length = r.ReadInt32();
				var data = r.ReadBytes(length);
				var p = new PacketInfo(data);
				p.Opcode = r.ReadUInt16();
				p.Time = DateTime.FromFileTimeUtc(r.ReadInt64());
				p.Source = (PacketInfo.Daemon)r.ReadByte();
				p.Destination = (PacketInfo.Daemon)r.ReadByte();

				result.Add(p);
			}

			r.Dispose();

			return result;
		}

		public static string Save(IEnumerable packets) {
			if (!Directory.Exists("log"))
				Directory.CreateDirectory("log");

			var file = $"log/{DateTime.Now.ToString("dd-MM-yy_HH.mm.ss")}.log";
			var f = File.Create(file);
			var w = new BinaryWriter(f);

			foreach (PacketInfo p in packets) {
				w.Write(p.Length);
				w.Write(p.Data);
				w.Write(p.Opcode);
				w.Write(p.Time.ToFileTimeUtc());
				w.Write((byte)p.Source);
				w.Write((byte)p.Destination);
			}

			w.Flush();
			w.Close();
			w.Dispose();

			return file;
		}
	}
}