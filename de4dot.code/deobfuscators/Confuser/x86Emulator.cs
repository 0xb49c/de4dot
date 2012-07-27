﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using de4dot.PE;

namespace de4dot.code.deobfuscators.Confuser {
	class x86Emulator {
		static readonly byte[] prolog = new byte[] {
			0x89, 0xE0, 0x53, 0x57, 0x56, 0x29, 0xE0, 0x83,
			0xF8, 0x18, 0x74, 0x07, 0x8B, 0x44, 0x24, 0x10,
			0x50, 0xEB, 0x01, 0x51,
		};
		static readonly byte[] epilog = new byte[] {
			0x5E, 0x5F, 0x5B, 0xC3,
		};

		PeImage peImage;
		BinaryReader reader;
		uint[] args;
		int nextArgIndex;
		uint[] regs = new uint[8];
		byte modRM, mod, reg, rm;

		enum OpCode {
			Add_RI,
			Add_RR,
			Mov_RI,
			Mov_RR,
			Neg_R,
			Not_R,
			Pop_R,
			Sub_RI,
			Sub_RR,
			Xor_RI,
			Xor_RR,
		}

		interface IOperand {
		}

		class RegOperand : IOperand {
			static readonly string[] names = new string[8] {
				"eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi",
			};
			public readonly int reg;

			public RegOperand(int reg) {
				this.reg = reg;
			}

			public override string ToString() {
				return names[reg];
			}
		}

		class ImmOperand : IOperand {
			public readonly int imm;

			public ImmOperand(int imm) {
				this.imm = imm;
			}

			public override string ToString() {
				return string.Format("{0:X2}h", imm);
			}
		}

		class Instruction {
			public readonly OpCode opCode;
			public IOperand op1;
			public IOperand op2;

			public Instruction(OpCode opCode)
				: this(opCode, null, null) {
			}

			public Instruction(OpCode opCode, IOperand op1)
				: this(opCode, op1, null) {
			}

			public Instruction(OpCode opCode, IOperand op1, IOperand op2) {
				this.opCode = opCode;
				this.op1 = op1;
				this.op2 = op2;
			}

			public override string ToString() {
				if (op1 != null && op2 != null)
					return string.Format("{0} {1},{2}", opCode, op1, op2);
				if (op1 != null)
					return string.Format("{0} {1}", opCode, op1);
				return string.Format("{0}", opCode);
			}
		}

		public x86Emulator(PeImage peImage) {
			this.peImage = peImage;
			this.reader = peImage.Reader;
		}

		public uint emulate(uint rva, uint arg) {
			return emulate(rva, new uint[] { arg });
		}

		public uint emulate(uint rva, uint[] args) {
			initialize(args);

			reader.BaseStream.Position = peImage.rvaToOffset(rva);
			if (!isBytes(prolog))
				throw new ApplicationException(string.Format("Missing prolog @ RVA {0:X8}", rva));
			reader.BaseStream.Position += prolog.Length;

			while (!isBytes(epilog))
				emulate();

			return regs[0];
		}

		void initialize(uint[] args) {
			this.args = args;
			nextArgIndex = 0;
			for (int i = 0; i < regs.Length; i++)
				regs[i] = 0;
		}

		bool isBytes(IList<byte> bytes) {
			long oldPos = reader.BaseStream.Position;
			bool result = true;
			for (int i = 0; i < bytes.Count; i++) {
				if (bytes[i] != reader.ReadByte()) {
					result = false;
					break;
				}
			}
			reader.BaseStream.Position = oldPos;
			return result;
		}

		void emulate() {
			var instr = decode();
			switch (instr.opCode) {
			case OpCode.Add_RI:
			case OpCode.Add_RR:
				writeReg(instr.op1, readOp(instr.op1) + readOp(instr.op2));
				break;

			case OpCode.Mov_RI:
			case OpCode.Mov_RR:
				writeReg(instr.op1, readOp(instr.op2));
				break;

			case OpCode.Neg_R:
				writeReg(instr.op1, (uint)-(int)readOp(instr.op1));
				break;

			case OpCode.Not_R:
				writeReg(instr.op1, ~readOp(instr.op1));
				break;

			case OpCode.Pop_R:
				writeReg(instr.op1, getNextArg());
				break;

			case OpCode.Sub_RI:
			case OpCode.Sub_RR:
				writeReg(instr.op1, readOp(instr.op1) - readOp(instr.op2));
				break;

			case OpCode.Xor_RI:
			case OpCode.Xor_RR:
				writeReg(instr.op1, readOp(instr.op1) ^ readOp(instr.op2));
				break;

			default: throw new NotSupportedException();
			}
		}

		uint getNextArg() {
			if (nextArgIndex >= args.Length)
				throw new ApplicationException("No more args");
			return args[nextArgIndex++];
		}

		void writeReg(IOperand op, uint val) {
			var regOp = (RegOperand)op;
			regs[regOp.reg] = val;
		}

		uint readOp(IOperand op) {
			var regOp = op as RegOperand;
			if (regOp != null)
				return regs[regOp.reg];

			var immOp = op as ImmOperand;
			if (immOp != null)
				return (uint)immOp.imm;

			throw new NotSupportedException();
		}

		Instruction decode() {
			byte opc = reader.ReadByte();
			switch (opc) {
			case 0x01:	// ADD Ed,Gd
				parseModRM();
				return new Instruction(OpCode.Add_RR, new RegOperand(rm), new RegOperand(reg));

			case 0x29:	// SUB Ed,Gd
				parseModRM();
				return new Instruction(OpCode.Sub_RR, new RegOperand(rm), new RegOperand(reg));

			case 0x31:	// XOR Ed,Gd
				parseModRM();
				return new Instruction(OpCode.Xor_RR, new RegOperand(rm), new RegOperand(reg));

			case 0x58:	// POP EAX
			case 0x59:	// POP ECX
			case 0x5A:	// POP EDX
			case 0x5B:	// POP EBX
			case 0x5C:	// POP ESP
			case 0x5D:	// POP EBP
			case 0x5E:	// POP ESI
			case 0x5F:	// POP EDI
				return new Instruction(OpCode.Pop_R, new RegOperand(opc - 0x58));

			case 0x81:	// Grp1 Ed,Id
				parseModRM();
				switch (reg) {
				case 0: return new Instruction(OpCode.Add_RI, new RegOperand(rm), new ImmOperand(reader.ReadInt32()));
				case 5: return new Instruction(OpCode.Sub_RI, new RegOperand(rm), new ImmOperand(reader.ReadInt32()));
				case 6: return new Instruction(OpCode.Xor_RI, new RegOperand(rm), new ImmOperand(reader.ReadInt32()));
				default: throw new NotSupportedException();
				}

			case 0x89:	// MOV Ed,Gd
				parseModRM();
				return new Instruction(OpCode.Mov_RR, new RegOperand(rm), new RegOperand(reg));

			case 0xB8:	// MOV EAX,Id
			case 0xB9:	// MOV ECX,Id
			case 0xBA:	// MOV EDX,Id
			case 0xBB:	// MOV EBX,Id
			case 0xBC:	// MOV ESP,Id
			case 0xBD:	// MOV EBP,Id
			case 0xBE:	// MOV ESI,Id
			case 0xBF:	// MOV EDI,Id
				return new Instruction(OpCode.Mov_RI, new RegOperand(opc - 0xB8), new ImmOperand(reader.ReadInt32()));

			case 0xF7:	// Grp3 Ev
				parseModRM();
				switch (reg) {
				case 2: return new Instruction(OpCode.Not_R, new RegOperand(rm));
				case 3: return new Instruction(OpCode.Neg_R, new RegOperand(rm));
				default: throw new NotSupportedException();
				}

			default: throw new NotSupportedException(string.Format("Invalid opcode: {0:X2}", opc));
			}
		}

		void parseModRM() {
			modRM = reader.ReadByte();
			mod = (byte)((modRM >> 6) & 7);
			reg = (byte)((modRM >> 3) & 7);
			rm = (byte)(modRM & 7);
			if (mod != 3)
				throw new ApplicationException("Memory operand");
		}
	}
}
