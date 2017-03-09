﻿/// Sharp 80 (c) Matthew Hamilton
/// Licensed Under GPL v3

using System;
using System.IO;
using System.Linq;

namespace Sharp80
{
    internal enum TapeStatus { Stopped, Reading, ReadEngaged, Writing, WriteEngaged, Waiting }
    internal enum Baud { Low, High }

    internal partial class Tape : ISerializable
    {
        private const int DEFAULT_BLANK_TAPE_LENGTH = 0x0800;
        private const int MAX_TAPE_LENGTH = 0x12000;

        // Values in ticks (1 tstate = 1000 ticks)
        // All determined empirically by M3 ROM write timing
        // Ranges and thresholds are positive to positive, so about twice
        // the single pulse duration
        private const ulong HIGH_SPEED_PULSE_ONE = 378000;
        private const ulong HIGH_SPEED_PULSE_ZERO = 771000;
        private const ulong HIGH_SPEED_ONE_DELTA_RANGE_MIN = 721000;
        private const ulong HIGH_SPEED_ONE_DELTA_RANGE_MAX = 797000;
        private const ulong HIGH_SPEED_THRESHOLD = 1200000;
        private const ulong HIGH_SPEED_ZERO_DELTA_RANGE_MIN = 1459000;
        private const ulong HIGH_SPEED_ZERO_DELTA_RANGE_MAX = 1861000;

        private const ulong LOW_SPEED_PULSE_NEGATIVE = 203000;
        private const ulong LOW_SPEED_PULSE_POSITIVE = 189000;
        private const ulong LOW_SPEED_POST_CLOCK_ONE = 1632000;
        private const ulong LOW_SPEED_POST_DATA_ONE = 1632000;
        private const ulong LOW_SPEED_POST_DATA_ZERO = 3669000;
        private const ulong LOW_SPEED_ONE_DELTA_RANGE_MIN = 1923000;
        private const ulong LOW_SPEED_ONE_DELTA_RANGE_MAX = 2281000;
        private const ulong LOW_SPEED_THRESHOLD = 3000000;
        private const ulong LOW_SPEED_ZERO_DELTA_RANGE_MIN = 3858000;
        private const ulong LOW_SPEED_ZERO_DELTA_RANGE_MAX = 4379000;

        private Computer computer;
        private InterruptManager intMgr;
        private Clock clock;
        private PulseReq readPulseReq = null;

        public string FilePath { get; set; }

        private byte[] data;
        private int byteCursor;
        private byte bitCursor;
        private bool isBlank;

        private bool motorOn = false;
        private bool motorOnSignal = false;
        private bool motorEngaged = false;
        private bool recordInvoked = false;

        private int consecutiveFiftyFives = 0;
        private int consecutiveZeros = 0;

        private ulong lastWritePositive;
        private ulong nextLastWritePositive;
        private PulsePolarity lastWritePolarity = PulsePolarity.Zero;

        private int highSpeedWriteEvidence = 0;
        private bool skippedLast = false;

        private Transition transition;

        public Baud Speed { get; private set; }
        public bool Changed { get; private set; }

        // CONSTRUCTOR

        public Tape(Computer Computer)
        {
            computer = Computer;
        }
        public void Initialize(Clock Clock, InterruptManager InterruptManager)
        {
            clock = Clock;
            Transition.Initialize(clock, Read);
            intMgr = InterruptManager;
            InitTape();
        }

        // OUTPUT

        public TapeStatus Status
        {
            get
            {
                if (MotorOn)
                    return recordInvoked ? TapeStatus.Writing : TapeStatus.Reading;
                else if (MotorEngaged)
                    return recordInvoked ? TapeStatus.WriteEngaged : TapeStatus.ReadEngaged;
                else if (MotorOnSignal)
                    return TapeStatus.Waiting;
                else
                    return TapeStatus.Stopped;
            }
        }
        public string StatusReport
        {
            get
            {
                if (MotorOn)
                {
                    return string.Format(@"{0:0000.0} {1:00.0%} {2} {3}", Counter, Percent, Speed == Baud.High ? "H" : "L", Status);
                }
                else if (MotorOnSignal)
                {
                    return string.Format(@"{0:0000.0} {1:00.0%} {2} Waiting", Counter, Percent);
                }
                else
                {
                    return string.Empty;
                }
            }
        }
        public string PulseStatus { get { return transition?.After.ToString() ?? String.Empty; } }
        public byte Value
        {
            get
            {
                byte ret = 0;
                if (transition?.FlipFlop ?? false)
                    ret |= 0x80;
                if ((transition?.LastNonZero ?? PulseState.None) == PulseState.Positive)
                    ret |= 0x01;
                return ret;
            }
        }
        public bool IsBlank { get { return isBlank; } }

        // MOTOR CONTROL

        public bool MotorOn
        {
            get { return motorOn; }
            set
            {
                if (motorOn != value)
                {
                    motorOn = value;
                    transition = null;
                    if (motorOn)
                        Update();
                }
            }
        }
        public bool MotorEngaged
        {
            get { return motorEngaged; }
            private set
            {
                motorEngaged = value;
                MotorOn = MotorEngaged && MotorOnSignal;
            }
        }
        public bool MotorOnSignal
        {
            get { return motorOnSignal; }
            set
            {
                motorOnSignal = value;
                MotorOn = MotorEngaged && MotorOnSignal;
            }
        }
        public float Counter { get { return byteCursor + ((7f - bitCursor) / 10); } }
        public float Percent { get { return (float)byteCursor / data.Length; } }

        // USER CONTROLS

        public bool LoadBlank()
        {
            return Load(String.Empty);
        }
        public bool Load(string Path)
        {
            Stop();

            byte[] bytes;
            FilePath = Path;
            if (String.IsNullOrWhiteSpace(Path) || Storage.IsFileNameToken(FilePath))
            {
                // init tape will take care of it
                bytes = null;
            }
            else if (!Storage.LoadBinaryFile(Path, out bytes) || bytes.Length < 0x100)
            {
                return false;
            }
            InitTape(bytes);
            return true;
        }
        public bool Save()
        {
            if (Storage.SaveBinaryFile(FilePath, data))
            {
                Changed = false;
                return true;
            }
            else
            {
                return false;
            }
        }
        public void Play()
        {
            MotorEngaged = true;
            recordInvoked = false;
        }
        public void Record()
        {
            MotorEngaged = true;
            recordInvoked = true;
        }
        public void Stop()
        {
            MotorEngaged = false;
            recordInvoked = false;
        }
        public void Eject()
        {
            InitTape();
        }
        public void Rewind()
        {
            bitCursor = 7;
            byteCursor = 0;
            recordInvoked = false;
            Stop();
        }

        // TAPE SETUP

        private void InitTape(byte[] Bytes = null)
        {
            if (Bytes == null)
            {
                FilePath = Storage.FILE_NAME_NEW;
                data = new byte[DEFAULT_BLANK_TAPE_LENGTH];
                isBlank = true;
            }
            else
            {
                data = Bytes;
                isBlank = data.All(b => b == 0x00);
            }
            Rewind();
            lastWritePositive = 1;
            nextLastWritePositive = 0;
        }

        // CURSOR CONTROL

        /// <summary>
        /// Move the cursor down the tape by one bit. If reading and a new byte is encountered,
        /// check to see if it indicates header values for high or low speed. Low speed headers
        /// are usually zeros, and high speed headers are usually 0x55 (or 0xAA if offset by a
        /// single bit)
        /// </summary>
        /// <returns></returns>
        private bool AdvanceCursor()
        {
            if (bitCursor == 0)
            {
                byteCursor++;
                bitCursor = 7;
            }
            else
            {
                bitCursor--;
            }
            if (byteCursor >= data.Length)
            {
                // When writing, we can dynamically resize the tape length up to a reasonable amount
                if (Status == TapeStatus.Writing && data.Length < MAX_TAPE_LENGTH)
                {
                    Array.Resize(ref data, Math.Min(MAX_TAPE_LENGTH, data.Length * 11 / 10)); // Grow by 10%
                }
                else
                {
                    Stop();
                    return false;
                }
            }
            if (bitCursor == 7 && Status == TapeStatus.Reading)
            {
                switch (data[byteCursor])
                {
                    case 0x55:
                    case 0xAA:
                        consecutiveFiftyFives++;
                        break;
                    case 0x00:
                        consecutiveZeros++;
                        break;
                    default:
                        consecutiveFiftyFives = 0;
                        consecutiveZeros = 0;
                        break;
                }
                if (consecutiveFiftyFives > 20)
                    Speed = Baud.High;
                else if (consecutiveZeros > 20)
                    Speed = Baud.Low;
            }
            return true;
        }

        // READ OPERATIONS

        /// <summary>
        /// Keep checking the transitions when reading
        /// </summary>
        private void Update()
        {
            if (Status == TapeStatus.Reading)
            {
                transition = transition ?? new Transition(Speed);
                while (transition.Update(Speed))
                {
                    if (transition.IsRising) intMgr.CassetteRisingEdgeLatch.Latch();
                    else if (transition.IsFalling) intMgr.CassetteFallingEdgeLatch.Latch();
                }
                // Keep coming back as long as we're in read status
                readPulseReq?.Expire();
                computer.RegisterPulseReq(readPulseReq = new PulseReq(PulseReq.DelayBasis.Ticks,
                                                                      transition.TicksUntilNext,
                                                                      Update));
            }
        }
        private bool Read()
        {
            if (AdvanceCursor()) { return data[byteCursor].IsBitSet(bitCursor); }
            else { return false; }
        }

        // WRITE OPERATIONS

        public void HandleCasPort(byte b)
        {
            if (MotorOn)
            {
                transition?.ClearFlipFlop();
                var polarity = GetPolarity(b);
                if (lastWritePolarity == polarity)
                    return;
                lastWritePolarity = polarity;

                if (polarity == PulsePolarity.Positive)
                {
                    nextLastWritePositive = lastWritePositive;
                    lastWritePositive = clock.TickCount;

                    if (Status == TapeStatus.Writing)
                    {
                        // Check to see, are the pulse lengths within 5% of
                        // those written by trs80 rom routines?
                        var posDelta = lastWritePositive - nextLastWritePositive;
                        if (posDelta.IsBetween(HIGH_SPEED_ONE_DELTA_RANGE_MIN, HIGH_SPEED_ONE_DELTA_RANGE_MAX) ||
                            posDelta.IsBetween(HIGH_SPEED_ZERO_DELTA_RANGE_MIN, HIGH_SPEED_ZERO_DELTA_RANGE_MAX))
                        {
                            // This is a high speed pulse
                            highSpeedWriteEvidence++;
                            if (highSpeedWriteEvidence > 8)
                            {
                                Speed = Baud.High;
                                highSpeedWriteEvidence = Math.Min(highSpeedWriteEvidence, 16);
                                // short means a one, long means zero
                                Write(posDelta < HIGH_SPEED_THRESHOLD);
                            }
                        }
                        else if ((posDelta.IsBetween(LOW_SPEED_ONE_DELTA_RANGE_MIN, LOW_SPEED_ONE_DELTA_RANGE_MAX)) ||
                                 (posDelta.IsBetween(LOW_SPEED_ZERO_DELTA_RANGE_MIN, LOW_SPEED_ZERO_DELTA_RANGE_MAX)))
                        {
                            // This is a low speed pulse
                            highSpeedWriteEvidence--;
                            if (highSpeedWriteEvidence < -8)
                            {
                                Speed = Baud.Low;
                                highSpeedWriteEvidence = Math.Max(highSpeedWriteEvidence, -16);

                                if (posDelta > LOW_SPEED_THRESHOLD)
                                {
                                    if (skippedLast)
                                    {
                                        // sync error since we saw a short (clock) last time
                                        // but anything after a short clk is a one
                                        // M3 rom does this when writing the A5 marker in CSAVE (bug?)
                                        Write(true);
                                        skippedLast = false;
                                    }
                                    else
                                    {
                                        // long pulse means we only saw the clock pulse so this is a zero
                                        Write(false);
                                    }
                                }
                                else if (skippedLast)
                                {
                                    // we saw the clock pulse before and now this is data pulse one
                                    skippedLast = false;
                                    Write(true);
                                }
                                else
                                {
                                    // this is the clock pulse, skip it
                                    skippedLast = true;
                                }
                            }
                        }
                    }
                }
            }
        }
        private PulsePolarity GetPolarity(byte Input)
        {
            switch (Input & 0x03)
            {
                case 1:
                    return PulsePolarity.Positive;
                case 2:
                    return PulsePolarity.Negative;
                default:
                    return PulsePolarity.Zero;
            }
        }
        private void Write(bool Value)
        {
            if (AdvanceCursor())
            {
                if (Value)
                    data[byteCursor] = data[byteCursor].SetBit(bitCursor);
                else
                    data[byteCursor] = data[byteCursor].ResetBit(bitCursor);
                Changed = true;
                isBlank &= !Value;
            }
        }

        // MISC

        // SNAPSHOT SUPPORT

        public void Deserialize(BinaryReader Reader)
        {
            Speed = (Baud)Reader.ReadInt32();
            Changed = Reader.ReadBoolean();
            FilePath = Reader.ReadString();
            data = Reader.ReadBytes(Reader.ReadInt32());
            isBlank = Reader.ReadBoolean();
            byteCursor = Reader.ReadInt32();
            bitCursor = Reader.ReadByte();
            motorOn = Reader.ReadBoolean();
            motorOnSignal = Reader.ReadBoolean();
            motorEngaged = Reader.ReadBoolean();
            recordInvoked = Reader.ReadBoolean();
            consecutiveFiftyFives = Reader.ReadInt32();
            consecutiveZeros = Reader.ReadInt32();
            lastWritePositive = Reader.ReadUInt64();
            nextLastWritePositive = Reader.ReadUInt64();
            lastWritePolarity = (PulsePolarity)Reader.ReadInt32();
            highSpeedWriteEvidence = Reader.ReadInt32();
            skippedLast = Reader.ReadBoolean();
            if (Reader.ReadBoolean())
            {
                transition = transition ?? new Transition(Speed);
                transition.Deserialize(Reader);
            }
            else
            {
                transition = null;
            }
            if (Reader.ReadBoolean())
            {
                readPulseReq = readPulseReq ?? new PulseReq();
                readPulseReq.Deserialize(Reader, Update);
                if (readPulseReq.Active)
                    computer.AddPulseReq(readPulseReq);
            }
            else
            {
                readPulseReq = null;
            }
        }
        public void Serialize(BinaryWriter Writer)
        {
            Writer.Write((int)Speed);
            Writer.Write(Changed);
            Writer.Write(FilePath);
            Writer.Write(data.Length);
            Writer.Write(data);
            Writer.Write(isBlank);
            Writer.Write(byteCursor);
            Writer.Write(bitCursor);
            Writer.Write(motorOn);
            Writer.Write(motorOnSignal);
            Writer.Write(motorEngaged);
            Writer.Write(recordInvoked);
            Writer.Write(consecutiveFiftyFives);
            Writer.Write(consecutiveZeros);
            Writer.Write(lastWritePositive);
            Writer.Write(nextLastWritePositive);
            Writer.Write((int)lastWritePolarity);
            Writer.Write(highSpeedWriteEvidence);
            Writer.Write(skippedLast);
            Writer.Write(transition != null);
            if (transition != null)
                transition.Serialize(Writer);
            Writer.Write(readPulseReq != null);
            if (readPulseReq != null)
                readPulseReq.Serialize(Writer);
        }
    }
}