﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace redump2xe
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("usage: redump2xe.exe redumpstyle.cue");
                return;
            }

            foreach (var arg in args)
            {
                string cue = arg;
                string dir = new FileInfo(cue).DirectoryName;
                string xe_file = cue.Replace(".cue", ".xe");

                string[] cuesheet = File.ReadAllLines(cue);

                string[] files = cuesheet.Where(x => x.StartsWith("FILE")).Select(x => Regex.Match(x, "(?<=(FILE \")).*(?=(\" BINARY))").Value).ToArray();
                string[] indexes = cuesheet.Where(x => x.Contains("INDEX 01")).Select(x => Regex.Match(x, "[0-9]{2}:[0-9]{2}:[0-9]{2}").Value).ToArray();
                string[] tracks = cuesheet.Where(x => x.Contains("TRACK")).Select(x => Regex.Match(x, "(?<=(TRACK [0-9]{2} )).*").Value).ToArray();

                MemoryStream full_subchannel = GenerateFullSubchannel(dir, files, indexes, tracks);
                BinaryReader subr = new BinaryReader(full_subchannel);

                BinaryWriter bw = new BinaryWriter(new FileStream(xe_file, FileMode.Create));

                WriteXeHeader(bw, dir, files, indexes, tracks);

                //pregap gen
                GeneratePreGap(bw, cuesheet);

                int msf_absolute = 150;

                for (int i = 0; i < files.Length; i++)
                {
                    string file = Path.Combine(dir, files[i]);

                    BinaryReader br = new BinaryReader(new FileStream(file, FileMode.Open));

                    int msf_relative = 0;

                    while (br.BaseStream.Position != br.BaseStream.Length)
                    {
                        byte[] temp = br.ReadBytes(2352);
                        bw.Write(temp);

                        //dummy subchannel gen
                        //byte[] subchannel = GenerateSub(msf_relative, msf_absolute, (byte)(i + 1), 1);

                        byte[] subchannel = TransSubchannel(subr.ReadBytes(96));


                        bw.Write(subchannel);

                        msf_relative++;
                        msf_absolute++;
                    }

                    br.Close();
                }

                bw.Close();
                subr.Close();
            }
        }

        private static byte[] TransSubchannel(byte[] subchannel)
        {
            byte[] subchannel_trans = new byte[96];

            for (int k = 0; k < 8; k++)
            {
                for (int i = 0; i < 12; i++)
                {
                    for (int j = 0; j < 8; j++)
                    {
                        subchannel_trans[(7 - j) + i * 8] |= (byte)(((subchannel[i + k * 12] & (1 << j)) >> j) << 7 - k);
                    }
                }
            }

            return subchannel_trans;
        }

        private static MemoryStream GenerateFullSubchannel(string dir, string[] files, string[] indexes, string[] tracks)
        {
            MemoryStream sub_mem = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(sub_mem);

            int msf_relative = 0;
            int msf_absolute = 150;

            for (int i = 0; i < files.Length; i++)
            {
                string file = Path.Combine(dir, files[i]);
                long file_length = new FileInfo(file).Length / 2352;

                int gap = MSF2LBA(indexes[i]);

                byte track_n = LBA2MSF.MSFTable[i + 1];
                byte track_type = TrackType(tracks[i]);

                msf_relative = gap;

                for (int j = 0; j < gap; j++)
                {
                    byte[] subchannel_gap = new byte[96];

                    for (int k = 0; k < 12; k++)
                    {
                        subchannel_gap[k] = 0xff;
                    }

                    subchannel_gap[12 + 0] = track_type;
                    subchannel_gap[12 + 1] = track_n;
                    subchannel_gap[12 + 2] = 0;

                    subchannel_gap[12 + 3] = LBA2MSF.M(msf_relative);
                    subchannel_gap[12 + 4] = LBA2MSF.S(msf_relative);
                    subchannel_gap[12 + 5] = LBA2MSF.F(msf_relative);
                    subchannel_gap[12 + 6] = 0;

                    subchannel_gap[12 + 7] = LBA2MSF.M(msf_absolute);
                    subchannel_gap[12 + 8] = LBA2MSF.S(msf_absolute);
                    subchannel_gap[12 + 9] = LBA2MSF.F(msf_absolute);

                    byte[] crc = BitConverter.GetBytes(CRC.CalculateCRC16(subchannel_gap, 12, 10));

                    subchannel_gap[12 + 10] = crc[1];
                    subchannel_gap[12 + 11] = crc[0];

                    msf_relative--;
                    msf_absolute++;

                    bw.Write(subchannel_gap);
                }

                msf_relative = 0;

                for (int j = 0; j < file_length - gap; j++)
                {
                    byte[] subchannel = new byte[96];

                    if (msf_relative == 0)
                    {
                        for (int k = 0; k < 12; k++)
                        {
                            subchannel[k] = 0xff;
                        }
                    }

                    subchannel[12 + 0] = track_type;
                    subchannel[12 + 1] = track_n;
                    subchannel[12 + 2] = 1;

                    subchannel[12 + 3] = LBA2MSF.M(msf_relative);
                    subchannel[12 + 4] = LBA2MSF.S(msf_relative);
                    subchannel[12 + 5] = LBA2MSF.F(msf_relative);
                    subchannel[12 + 6] = 0;

                    subchannel[12 + 7] = LBA2MSF.M(msf_absolute);
                    subchannel[12 + 8] = LBA2MSF.S(msf_absolute);
                    subchannel[12 + 9] = LBA2MSF.F(msf_absolute);

                    byte[] crc = BitConverter.GetBytes(CRC.CalculateCRC16(subchannel, 12, 10));

                    subchannel[12 + 10] = crc[1];
                    subchannel[12 + 11] = crc[0];

                    msf_relative++;
                    msf_absolute++;

                    bw.Write(subchannel);
                }


            }
            sub_mem.Position = 0;
            return sub_mem;
        }

        private static int MSF2LBA(string index)
        {
            string[] MSF = Regex.Split(index, ":");
            int temp = Convert.ToInt32(MSF[0]) * 4500;
            temp += Convert.ToInt32(MSF[1]) * 75;
            temp += Convert.ToInt32(MSF[2]);
            return temp;
        }

        private static void GeneratePreGap(BinaryWriter bw, string[] cuesheet)
        {
            string _temp = cuesheet.Single(x => x.Contains("MODE")).Trim();

            byte mode = Convert.ToByte(Regex.Match(_temp, "(?<=(MODE))[\\d]{1}").Value);
            int sectorsize = Convert.ToInt32(Regex.Match(_temp, "(?<=(/))[\\d]{4}").Value);

            byte[] pregap = new byte[150 * (sectorsize + 96)];

            int msf_relative = 149;
            int msf_absolute = 1;

            for (int i = 0; i < 150; i++)
            {
                for (int j = 0; j < 10; j++)
                {
                    pregap[i * (2352 + 96) + j + 1] = 0xff;
                }

                pregap[i * (2352 + 96) + 12] = LBA2MSF.M(i);
                pregap[i * (2352 + 96) + 13] = LBA2MSF.S(i);
                pregap[i * (2352 + 96) + 14] = LBA2MSF.F(i);
                pregap[i * (2352 + 96) + 15] = mode;

                CRC.CalculateEDC(pregap, i * (2352 + 96), mode);
                CRC.CalculateECC(pregap, i * (2352 + 96));

                byte[] subchannel = GenerateSub(msf_relative, msf_absolute, 1, 0);

                for (int k = 0; k < 96; k++)
                {
                    pregap[i * (2352 + 96) + 2352 + k] = subchannel[k];
                }

                msf_relative--;
                msf_absolute++;
            }

            bw.Write(pregap);
        }

        private static byte[] GenerateSub(int msf_relative, int msf_absolute, byte track_n, byte index)
        {
            byte[] subchannel = new byte[96];
            byte[] trans_subchannel = new byte[96];

            if (msf_relative == 0 && index == 1)
            {
                for (int i = 0; i < 12; i++)
                {
                    subchannel[i] = 0xff;
                }
            }

            subchannel[12 + 0] = 0x41;
            subchannel[12 + 1] = track_n;
            subchannel[12 + 2] = index;

            subchannel[12 + 3] = LBA2MSF.M(msf_relative);
            subchannel[12 + 4] = LBA2MSF.S(msf_relative);
            subchannel[12 + 5] = LBA2MSF.F(msf_relative);
            subchannel[12 + 6] = 0;

            subchannel[12 + 7] = LBA2MSF.M(msf_absolute);
            subchannel[12 + 8] = LBA2MSF.S(msf_absolute);
            subchannel[12 + 9] = LBA2MSF.F(msf_absolute);

            byte[] crc = BitConverter.GetBytes(CRC.CalculateCRC16(subchannel, 12, 10));

            subchannel[12 + 10] = crc[1];
            subchannel[12 + 11] = crc[0];

            for (int k = 0; k < 8; k++)
            {
                for (int i = 0; i < 12; i++)
                {
                    for (int j = 0; j < 8; j++)
                    {
                        trans_subchannel[(7 - j) + i * 8] |= (byte)(((subchannel[i + k * 12] & (1 << j)) >> j) << 7 - k);
                    }
                }
            }

            return trans_subchannel;
        }



        private static void WriteXeHeader(BinaryWriter bw, string dir, string[] files, string[] indexes, string[] tracks)
        {
            byte[] header = new byte[0x200];

            header[0] = 0;
            header[1] = 0;
            header[2] = 1;
            header[3] = (byte)(files.Length);

            long start = 150;

            for (int i = 0; i < files.Length; i++)
            {
                int index = TrackIndex(indexes[i]);

                header[(i * 4) + 4] = TrackType(tracks[i]);
                header[(i * 4) + 5] = MSF.M(start + index);
                header[(i * 4) + 6] = MSF.S(start + index);
                header[(i * 4) + 7] = MSF.F(start + index);

                long track_length = new FileInfo(Path.Combine(dir, files[i])).Length / 2352;

                start += track_length;
            }

            //lead_out
            header[files.Length * 4 + 4] = 0x16;
            header[files.Length * 4 + 5] = MSF.M(start);
            header[files.Length * 4 + 6] = MSF.S(start);
            header[files.Length * 4 + 7] = MSF.F(start);

            bw.Write(header);
        }

        private static int TrackIndex(string index)
        {
            string[] MSF = Regex.Split(index, ":");
            int temp = Convert.ToInt32(MSF[0]) * 4500;
            temp += Convert.ToInt32(MSF[1]) * 75;
            temp += Convert.ToInt32(MSF[2]);
            return temp;
        }

        private static byte TrackType(string track)
        {
            if (track.Contains("AUDIO")) return 0x10;
            return 0x14;
        }
    }

    internal class CRC
    {
        static readonly uint[] edc_lut =
        {
            0x00000000, 0x90910101, 0x91210201, 0x01b00300, 0x92410401, 0x02d00500, 0x03600600, 0x93f10701,
            0x94810801, 0x04100900, 0x05a00a00, 0x95310b01, 0x06c00c00, 0x96510d01, 0x97e10e01, 0x07700f00,
            0x99011001, 0x09901100, 0x08201200, 0x98b11301, 0x0b401400, 0x9bd11501, 0x9a611601, 0x0af01700,
            0x0d801800, 0x9d111901, 0x9ca11a01, 0x0c301b00, 0x9fc11c01, 0x0f501d00, 0x0ee01e00, 0x9e711f01,
            0x82012001, 0x12902100, 0x13202200, 0x83b12301, 0x10402400, 0x80d12501, 0x81612601, 0x11f02700,
            0x16802800, 0x86112901, 0x87a12a01, 0x17302b00, 0x84c12c01, 0x14502d00, 0x15e02e00, 0x85712f01,
            0x1b003000, 0x8b913101, 0x8a213201, 0x1ab03300, 0x89413401, 0x19d03500, 0x18603600, 0x88f13701,
            0x8f813801, 0x1f103900, 0x1ea03a00, 0x8e313b01, 0x1dc03c00, 0x8d513d01, 0x8ce13e01, 0x1c703f00,
            0xb4014001, 0x24904100, 0x25204200, 0xb5b14301, 0x26404400, 0xb6d14501, 0xb7614601, 0x27f04700,
            0x20804800, 0xb0114901, 0xb1a14a01, 0x21304b00, 0xb2c14c01, 0x22504d00, 0x23e04e00, 0xb3714f01,
            0x2d005000, 0xbd915101, 0xbc215201, 0x2cb05300, 0xbf415401, 0x2fd05500, 0x2e605600, 0xbef15701,
            0xb9815801, 0x29105900, 0x28a05a00, 0xb8315b01, 0x2bc05c00, 0xbb515d01, 0xbae15e01, 0x2a705f00,
            0x36006000, 0xa6916101, 0xa7216201, 0x37b06300, 0xa4416401, 0x34d06500, 0x35606600, 0xa5f16701,
            0xa2816801, 0x32106900, 0x33a06a00, 0xa3316b01, 0x30c06c00, 0xa0516d01, 0xa1e16e01, 0x31706f00,
            0xaf017001, 0x3f907100, 0x3e207200, 0xaeb17301, 0x3d407400, 0xadd17501, 0xac617601, 0x3cf07700,
            0x3b807800, 0xab117901, 0xaaa17a01, 0x3a307b00, 0xa9c17c01, 0x39507d00, 0x38e07e00, 0xa8717f01,
            0xd8018001, 0x48908100, 0x49208200, 0xd9b18301, 0x4a408400, 0xdad18501, 0xdb618601, 0x4bf08700,
            0x4c808800, 0xdc118901, 0xdda18a01, 0x4d308b00, 0xdec18c01, 0x4e508d00, 0x4fe08e00, 0xdf718f01,
            0x41009000, 0xd1919101, 0xd0219201, 0x40b09300, 0xd3419401, 0x43d09500, 0x42609600, 0xd2f19701,
            0xd5819801, 0x45109900, 0x44a09a00, 0xd4319b01, 0x47c09c00, 0xd7519d01, 0xd6e19e01, 0x46709f00,
            0x5a00a000, 0xca91a101, 0xcb21a201, 0x5bb0a300, 0xc841a401, 0x58d0a500, 0x5960a600, 0xc9f1a701,
            0xce81a801, 0x5e10a900, 0x5fa0aa00, 0xcf31ab01, 0x5cc0ac00, 0xcc51ad01, 0xcde1ae01, 0x5d70af00,
            0xc301b001, 0x5390b100, 0x5220b200, 0xc2b1b301, 0x5140b400, 0xc1d1b501, 0xc061b601, 0x50f0b700,
            0x5780b800, 0xc711b901, 0xc6a1ba01, 0x5630bb00, 0xc5c1bc01, 0x5550bd00, 0x54e0be00, 0xc471bf01,
            0x6c00c000, 0xfc91c101, 0xfd21c201, 0x6db0c300, 0xfe41c401, 0x6ed0c500, 0x6f60c600, 0xfff1c701,
            0xf881c801, 0x6810c900, 0x69a0ca00, 0xf931cb01, 0x6ac0cc00, 0xfa51cd01, 0xfbe1ce01, 0x6b70cf00,
            0xf501d001, 0x6590d100, 0x6420d200, 0xf4b1d301, 0x6740d400, 0xf7d1d501, 0xf661d601, 0x66f0d700,
            0x6180d800, 0xf111d901, 0xf0a1da01, 0x6030db00, 0xf3c1dc01, 0x6350dd00, 0x62e0de00, 0xf271df01,
            0xee01e001, 0x7e90e100, 0x7f20e200, 0xefb1e301, 0x7c40e400, 0xecd1e501, 0xed61e601, 0x7df0e700,
            0x7a80e800, 0xea11e901, 0xeba1ea01, 0x7b30eb00, 0xe8c1ec01, 0x7850ed00, 0x79e0ee00, 0xe971ef01,
            0x7700f000, 0xe791f101, 0xe621f201, 0x76b0f300, 0xe541f401, 0x75d0f500, 0x7460f600, 0xe4f1f701,
            0xe381f801, 0x7310f900, 0x72a0fa00, 0xe231fb01, 0x71c0fc00, 0xe151fd01, 0xe0e1fe01, 0x7070ff00
        };

        static readonly byte[] ecc_b_lut =
        {
            0x00, 0xf4, 0xf5, 0x01, 0xf7, 0x03, 0x02, 0xf6, 0xf3, 0x07, 0x06, 0xf2, 0x04, 0xf0, 0xf1, 0x05,
            0xfb, 0x0f, 0x0e, 0xfa, 0x0c, 0xf8, 0xf9, 0x0d, 0x08, 0xfc, 0xfd, 0x09, 0xff, 0x0b, 0x0a, 0xfe,
            0xeb, 0x1f, 0x1e, 0xea, 0x1c, 0xe8, 0xe9, 0x1d, 0x18, 0xec, 0xed, 0x19, 0xef, 0x1b, 0x1a, 0xee,
            0x10, 0xe4, 0xe5, 0x11, 0xe7, 0x13, 0x12, 0xe6, 0xe3, 0x17, 0x16, 0xe2, 0x14, 0xe0, 0xe1, 0x15,
            0xcb, 0x3f, 0x3e, 0xca, 0x3c, 0xc8, 0xc9, 0x3d, 0x38, 0xcc, 0xcd, 0x39, 0xcf, 0x3b, 0x3a, 0xce,
            0x30, 0xc4, 0xc5, 0x31, 0xc7, 0x33, 0x32, 0xc6, 0xc3, 0x37, 0x36, 0xc2, 0x34, 0xc0, 0xc1, 0x35,
            0x20, 0xd4, 0xd5, 0x21, 0xd7, 0x23, 0x22, 0xd6, 0xd3, 0x27, 0x26, 0xd2, 0x24, 0xd0, 0xd1, 0x25,
            0xdb, 0x2f, 0x2e, 0xda, 0x2c, 0xd8, 0xd9, 0x2d, 0x28, 0xdc, 0xdd, 0x29, 0xdf, 0x2b, 0x2a, 0xde,
            0x8b, 0x7f, 0x7e, 0x8a, 0x7c, 0x88, 0x89, 0x7d, 0x78, 0x8c, 0x8d, 0x79, 0x8f, 0x7b, 0x7a, 0x8e,
            0x70, 0x84, 0x85, 0x71, 0x87, 0x73, 0x72, 0x86, 0x83, 0x77, 0x76, 0x82, 0x74, 0x80, 0x81, 0x75,
            0x60, 0x94, 0x95, 0x61, 0x97, 0x63, 0x62, 0x96, 0x93, 0x67, 0x66, 0x92, 0x64, 0x90, 0x91, 0x65,
            0x9b, 0x6f, 0x6e, 0x9a, 0x6c, 0x98, 0x99, 0x6d, 0x68, 0x9c, 0x9d, 0x69, 0x9f, 0x6b, 0x6a, 0x9e,
            0x40, 0xb4, 0xb5, 0x41, 0xb7, 0x43, 0x42, 0xb6, 0xb3, 0x47, 0x46, 0xb2, 0x44, 0xb0, 0xb1, 0x45,
            0xbb, 0x4f, 0x4e, 0xba, 0x4c, 0xb8, 0xb9, 0x4d, 0x48, 0xbc, 0xbd, 0x49, 0xbf, 0x4b, 0x4a, 0xbe,
            0xab, 0x5f, 0x5e, 0xaa, 0x5c, 0xa8, 0xa9, 0x5d, 0x58, 0xac, 0xad, 0x59, 0xaf, 0x5b, 0x5a, 0xae,
            0x50, 0xa4, 0xa5, 0x51, 0xa7, 0x53, 0x52, 0xa6, 0xa3, 0x57, 0x56, 0xa2, 0x54, 0xa0, 0xa1, 0x55
        };

        static readonly byte[] ecc_f_lut =
        {
            0x00, 0x02, 0x04, 0x06, 0x08, 0x0a, 0x0c, 0x0e, 0x10, 0x12, 0x14, 0x16, 0x18, 0x1a, 0x1c, 0x1e,
            0x20, 0x22, 0x24, 0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30, 0x32, 0x34, 0x36, 0x38, 0x3a, 0x3c, 0x3e,
            0x40, 0x42, 0x44, 0x46, 0x48, 0x4a, 0x4c, 0x4e, 0x50, 0x52, 0x54, 0x56, 0x58, 0x5a, 0x5c, 0x5e,
            0x60, 0x62, 0x64, 0x66, 0x68, 0x6a, 0x6c, 0x6e, 0x70, 0x72, 0x74, 0x76, 0x78, 0x7a, 0x7c, 0x7e,
            0x80, 0x82, 0x84, 0x86, 0x88, 0x8a, 0x8c, 0x8e, 0x90, 0x92, 0x94, 0x96, 0x98, 0x9a, 0x9c, 0x9e,
            0xa0, 0xa2, 0xa4, 0xa6, 0xa8, 0xaa, 0xac, 0xae, 0xb0, 0xb2, 0xb4, 0xb6, 0xb8, 0xba, 0xbc, 0xbe,
            0xc0, 0xc2, 0xc4, 0xc6, 0xc8, 0xca, 0xcc, 0xce, 0xd0, 0xd2, 0xd4, 0xd6, 0xd8, 0xda, 0xdc, 0xde,
            0xe0, 0xe2, 0xe4, 0xe6, 0xe8, 0xea, 0xec, 0xee, 0xf0, 0xf2, 0xf4, 0xf6, 0xf8, 0xfa, 0xfc, 0xfe,
            0x1d, 0x1f, 0x19, 0x1b, 0x15, 0x17, 0x11, 0x13, 0x0d, 0x0f, 0x09, 0x0b, 0x05, 0x07, 0x01, 0x03,
            0x3d, 0x3f, 0x39, 0x3b, 0x35, 0x37, 0x31, 0x33, 0x2d, 0x2f, 0x29, 0x2b, 0x25, 0x27, 0x21, 0x23,
            0x5d, 0x5f, 0x59, 0x5b, 0x55, 0x57, 0x51, 0x53, 0x4d, 0x4f, 0x49, 0x4b, 0x45, 0x47, 0x41, 0x43,
            0x7d, 0x7f, 0x79, 0x7b, 0x75, 0x77, 0x71, 0x73, 0x6d, 0x6f, 0x69, 0x6b, 0x65, 0x67, 0x61, 0x63,
            0x9d, 0x9f, 0x99, 0x9b, 0x95, 0x97, 0x91, 0x93, 0x8d, 0x8f, 0x89, 0x8b, 0x85, 0x87, 0x81, 0x83,
            0xbd, 0xbf, 0xb9, 0xbb, 0xb5, 0xb7, 0xb1, 0xb3, 0xad, 0xaf, 0xa9, 0xab, 0xa5, 0xa7, 0xa1, 0xa3,
            0xdd, 0xdf, 0xd9, 0xdb, 0xd5, 0xd7, 0xd1, 0xd3, 0xcd, 0xcf, 0xc9, 0xcb, 0xc5, 0xc7, 0xc1, 0xc3,
            0xfd, 0xff, 0xf9, 0xfb, 0xf5, 0xf7, 0xf1, 0xf3, 0xed, 0xef, 0xe9, 0xeb, 0xe5, 0xe7, 0xe1, 0xe3,
        };
        static readonly UInt16[] table = {
            0x0000, 0x1021, 0x2042, 0x3063, 0x4084, 0x50a5, 0x60c6, 0x70e7,
            0x8108, 0x9129, 0xa14a, 0xb16b, 0xc18c, 0xd1ad, 0xe1ce, 0xf1ef,
            0x1231, 0x0210, 0x3273, 0x2252, 0x52b5, 0x4294, 0x72f7, 0x62d6,
            0x9339, 0x8318, 0xb37b, 0xa35a, 0xd3bd, 0xc39c, 0xf3ff, 0xe3de,
            0x2462, 0x3443, 0x0420, 0x1401, 0x64e6, 0x74c7, 0x44a4, 0x5485,
            0xa56a, 0xb54b, 0x8528, 0x9509, 0xe5ee, 0xf5cf, 0xc5ac, 0xd58d,
            0x3653, 0x2672, 0x1611, 0x0630, 0x76d7, 0x66f6, 0x5695, 0x46b4,
            0xb75b, 0xa77a, 0x9719, 0x8738, 0xf7df, 0xe7fe, 0xd79d, 0xc7bc,
            0x48c4, 0x58e5, 0x6886, 0x78a7, 0x0840, 0x1861, 0x2802, 0x3823,
            0xc9cc, 0xd9ed, 0xe98e, 0xf9af, 0x8948, 0x9969, 0xa90a, 0xb92b,
            0x5af5, 0x4ad4, 0x7ab7, 0x6a96, 0x1a71, 0x0a50, 0x3a33, 0x2a12,
            0xdbfd, 0xcbdc, 0xfbbf, 0xeb9e, 0x9b79, 0x8b58, 0xbb3b, 0xab1a,
            0x6ca6, 0x7c87, 0x4ce4, 0x5cc5, 0x2c22, 0x3c03, 0x0c60, 0x1c41,
            0xedae, 0xfd8f, 0xcdec, 0xddcd, 0xad2a, 0xbd0b, 0x8d68, 0x9d49,
            0x7e97, 0x6eb6, 0x5ed5, 0x4ef4, 0x3e13, 0x2e32, 0x1e51, 0x0e70,
            0xff9f, 0xefbe, 0xdfdd, 0xcffc, 0xbf1b, 0xaf3a, 0x9f59, 0x8f78,
            0x9188, 0x81a9, 0xb1ca, 0xa1eb, 0xd10c, 0xc12d, 0xf14e, 0xe16f,
            0x1080, 0x00a1, 0x30c2, 0x20e3, 0x5004, 0x4025, 0x7046, 0x6067,
            0x83b9, 0x9398, 0xa3fb, 0xb3da, 0xc33d, 0xd31c, 0xe37f, 0xf35e,
            0x02b1, 0x1290, 0x22f3, 0x32d2, 0x4235, 0x5214, 0x6277, 0x7256,
            0xb5ea, 0xa5cb, 0x95a8, 0x8589, 0xf56e, 0xe54f, 0xd52c, 0xc50d,
            0x34e2, 0x24c3, 0x14a0, 0x0481, 0x7466, 0x6447, 0x5424, 0x4405,
            0xa7db, 0xb7fa, 0x8799, 0x97b8, 0xe75f, 0xf77e, 0xc71d, 0xd73c,
            0x26d3, 0x36f2, 0x0691, 0x16b0, 0x6657, 0x7676, 0x4615, 0x5634,
            0xd94c, 0xc96d, 0xf90e, 0xe92f, 0x99c8, 0x89e9, 0xb98a, 0xa9ab,
            0x5844, 0x4865, 0x7806, 0x6827, 0x18c0, 0x08e1, 0x3882, 0x28a3,
            0xcb7d, 0xdb5c, 0xeb3f, 0xfb1e, 0x8bf9, 0x9bd8, 0xabbb, 0xbb9a,
            0x4a75, 0x5a54, 0x6a37, 0x7a16, 0x0af1, 0x1ad0, 0x2ab3, 0x3a92,
            0xfd2e, 0xed0f, 0xdd6c, 0xcd4d, 0xbdaa, 0xad8b, 0x9de8, 0x8dc9,
            0x7c26, 0x6c07, 0x5c64, 0x4c45, 0x3ca2, 0x2c83, 0x1ce0, 0x0cc1,
            0xef1f, 0xff3e, 0xcf5d, 0xdf7c, 0xaf9b, 0xbfba, 0x8fd9, 0x9ff8,
            0x6e17, 0x7e36, 0x4e55, 0x5e74, 0x2e93, 0x3eb2, 0x0ed1, 0x1ef0
        };

        internal static UInt16 CalculateCRC16(byte[] input, int offset, int length)
        {
            uint crc = 0;

            for (int i = offset; i < offset + length; i++)
            {
                crc = table[((crc >> 8) ^ input[i]) & 0xFF] ^ (crc << 8);
            }

            return (UInt16)~crc;
        }

        internal static void CalculateECC(byte[] pregap, int globaloffset)
        {
            UInt32 major_count, minor_count, major_mult, minor_inc;
            major_count = 86;
            minor_count = 24;
            major_mult = 2;
            minor_inc = 86;

            var eccsize = major_count * minor_count;
            UInt32 major, minor;
            for (major = 0; major < major_count; major++)
            {
                var index = (major >> 1) * major_mult + (major & 1);
                byte ecc_a = 0;
                byte ecc_b = 0;
                for (minor = 0; minor < minor_count; minor++)
                {
                    byte temp = pregap[globaloffset + 12 + index];
                    index += minor_inc;
                    if (index >= eccsize) index -= eccsize;
                    ecc_a ^= temp;
                    ecc_b ^= temp;
                    ecc_a = ecc_f_lut[ecc_a];
                }
                ecc_a = ecc_b_lut[ecc_f_lut[ecc_a] ^ ecc_b];
                pregap[globaloffset + 2076 + major] = ecc_a;
                pregap[globaloffset + 2076 + major + major_count] = (byte)(ecc_a ^ ecc_b);
            }

            major_count = 52;
            minor_count = 43;
            major_mult = 86;
            minor_inc = 88;

            eccsize = major_count * minor_count;
            for (major = 0; major < major_count; major++)
            {
                var index = (major >> 1) * major_mult + (major & 1);
                byte ecc_a = 0;
                byte ecc_b = 0;
                for (minor = 0; minor < minor_count; minor++)
                {
                    byte temp = pregap[globaloffset + 12 + index];
                    index += minor_inc;
                    if (index >= eccsize) index -= eccsize;
                    ecc_a ^= temp;
                    ecc_b ^= temp;
                    ecc_a = ecc_f_lut[ecc_a];
                }
                ecc_a = ecc_b_lut[ecc_f_lut[ecc_a] ^ ecc_b];
                pregap[globaloffset + 2076 + 172 + major] = ecc_a;
                pregap[globaloffset + 2076 + 172 + major + major_count] = (byte)(ecc_a ^ ecc_b);
            }
        }

        internal static void CalculateEDC(byte[] pregap, int globaloffset, byte mode)
        {
            UInt32 edc = 0;
            int count = 0;
            var i = 0;
            int offset = 0;

            switch (mode)
            {
                case 1:
                    count = 2064;
                    offset = 0;
                    break;
                case 2:
                    int form = pregap[globaloffset + 18] & 0x20;
                    switch (form)
                    {
                        default:
                            count = 2048 + 8;
                            offset = 16;
                            break;
                        case 0x20:
                            count = 2324 + 8;
                            offset = 16;
                            break;
                    }
                    break;
            }
            while (i != count)
            {
                edc = (UInt32)((edc >> 8) ^ CRC.edc_lut[(edc ^ (pregap[globaloffset + offset + i++])) & 0xff]);
            }
            byte[] ar_edc = BitConverter.GetBytes(edc);

            ar_edc.CopyTo(pregap, globaloffset + offset + count);
        }
    }

    internal class LBA2MSF
    {
        internal static readonly byte[] MSFTable = {
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
            0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
            0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
            0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99,
            0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9,
            0xb0, 0xb1, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9,
            0xc0, 0xc1, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9,
            0xd0, 0xd1, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9,
            0xe0, 0xe1, 0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9,
            0xf0, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9,
        };
        internal static byte M(int sector)
        {
            return MSFTable[(byte)(sector / 4500)];
        }
        internal static byte S(int sector)
        {
            return MSFTable[(byte)((sector / 75) % 60)];
        }
        internal static byte F(int sector)
        {
            return MSFTable[(byte)(sector % 75)];
        }
    }

    internal class MSF
    {
        internal static byte M(long track_length)
        {
            return (byte)(track_length / 4500);
        }
        internal static byte S(long track_length)
        {
            return (byte)((track_length / 75) % 60);
        }
        internal static byte F(long track_length)
        {
            return (byte)(track_length % 75);
        }
    }
}
