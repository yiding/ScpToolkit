﻿using System;
using System.Net.NetworkInformation;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ScpControl.ScpCore;
using ScpControl.Shared.Core;
using ScpControl.Sound;
using ScpControl.Utilities;

namespace ScpControl.Bluetooth {
  /// <summary>
  ///     Represents a generic Bluetooth client device.
  /// </summary>
  public class BthDevice : BthConnection, IDsDevice {
    #region Private fields

    private readonly IObservable<long> _outputReportSchedule = Observable.Interval(TimeSpan.FromMilliseconds(10),
        Scheduler.Default);

    private IDisposable _outputReportTask;

    private readonly TaskQueue _inputReportQueue = new TaskQueue();

    #endregion

    #region Protected fields

    protected bool m_Blocked, m_IsIdle = true, m_IsDisconnect;
    protected byte m_CableStatus = 0;
    protected readonly IBthDevice BluetoothDevice;
    protected byte m_Init = 0;

    protected DateTime m_Last = DateTime.Now,
        m_Idle = DateTime.Now,
        m_Tick = DateTime.Now,
        m_Disconnect = DateTime.Now;

    protected uint m_Packet;
    protected byte m_PlugStatus = 0;
    private bool m_Publish;
    protected uint m_Queued = 0;

    #endregion

    #region Public properties

    public DsState State { get; protected set; }

    public DsConnection Connection {
      get { return DsConnection.Bluetooth; }
    }

    public DsBattery Battery { get; protected set; }

    public PhysicalAddress HostAddress { get; private set; }

    public virtual DsPadId PadId { get; set; }

    public uint? XInputSlot { get; set; }

    #endregion

    #region Public methods

    public ScpHidReport NewHidReport() {
      return new ScpHidReport {
        PadId = PadId,
        PadState = State,
        ConnectionType = Connection,
        Model = Model,
        PadMacAddress = DeviceAddress,
        BatteryStatus = (byte)Battery
      };
    }

    public virtual bool Start() {
      _outputReportTask = _outputReportSchedule.Subscribe(tick => OnTimer());

      // play connection sound
      if (GlobalConfiguration.Instance.IsBluetoothConnectSoundEnabled)
        AudioPlayer.Instance.PlayCustomFile(GlobalConfiguration.Instance.BluetoothConnectSoundFile);

      return State == DsState.Connected;
    }

    public virtual bool Rumble(byte large, byte small) {
      return false;
    }

    public virtual bool Pair(PhysicalAddress master) {
      return false;
    }

    public virtual bool Disconnect() {
      m_Publish = false;
      return BluetoothDevice.HCI_Disconnect(HciHandle) > 0;
    }

    public void Stop() {
      if (State == DsState.Connected) {
        if (_outputReportTask != null)
          _outputReportTask.Dispose();

        State = GlobalConfiguration.Instance.ReservePadSlot ? DsState.Reserved : DsState.Disconnected;

        m_Packet = 0;

        m_Publish = false;
        OnHidReportReceived(NewHidReport());

        // play disconnect sound
        if (GlobalConfiguration.Instance.IsBluetoothDisconnectSoundEnabled)
          AudioPlayer.Instance.PlayCustomFile(GlobalConfiguration.Instance.BluetoothDisconnectSoundFile);
      }
    }

    public virtual bool Close() {
      Stop();

      if (State == DsState.Reserved) {
        State = DsState.Disconnected;
        m_Packet = 0;

        m_Publish = false;
        OnHidReportReceived(NewHidReport());
      }

      return State == DsState.Disconnected;
    }

    public virtual void ParseHidReport(byte[] report) {
    }

    public virtual bool InitHidReport(byte[] report) {
      return true;
    }

    public override string ToString() {
      switch (State) {
        case DsState.Disconnected:
          return string.Format("Pad {0} : Disconnected", PadId);

        case DsState.Reserved:
          return string.Format("Pad {0} : {1} {2} - Reserved", PadId, Model, DeviceAddress.AsFriendlyName());

        case DsState.Connected:
          return string.Format("Pad {0} : {1} {2} - {3} {4:X8} {5}", PadId, Model,
              DeviceAddress.AsFriendlyName(),
              Connection,
              m_Packet,
              Battery);
      }

      throw new Exception();
    }

    public virtual void Completed() {
      lock (this) {
        m_Blocked = false;
      }
    }

    #endregion

    #region Events

    public event EventHandler<ScpHidReport> HidReportReceived;

    protected void OnHidReportReceived(ScpHidReport report) {
      if (GlobalConfiguration.Instance.UseAsyncHidReportProcessing) {
        _inputReportQueue.Enqueue(() => Task.Run(() => {
          if (HidReportReceived != null)
            HidReportReceived.Invoke(this, report);
        }));
      } else {
        if (HidReportReceived != null)
          HidReportReceived.Invoke(this, report);
      }
    }

    #endregion

    #region Protected methods

    protected virtual void Process(DateTime now) {
    }

    private void OnTimer() {
      if (State != DsState.Connected) return;

      #region Calculate and trigger idle auto-disconnect

      var now = DateTime.Now;

      if (m_IsIdle && GlobalConfiguration.Instance.IdleDisconnect) {
        if ((now - m_Idle).TotalMilliseconds >= GlobalConfiguration.Instance.IdleTimeout) {
          Log.InfoFormat("Pad {0} disconnected due to idle timeout", PadId);

          m_IsDisconnect = false;
          m_IsIdle = false;

          Disconnect();
          return;
        }
      } else if (m_IsDisconnect) {
        if ((now - m_Disconnect).TotalMilliseconds >= 2000) {
          Log.InfoFormat("Pad {0} disconnected due to quick disconnect combo", PadId);

          m_IsDisconnect = false;
          m_IsIdle = false;

          Disconnect();
          return;
        }
      }

      #endregion

      Process(now);
    }

    #endregion

    #region Ctors

    protected BthDevice() {
      DeviceAddress = PhysicalAddress.None;
      HostAddress = PhysicalAddress.None;
    }

    protected BthDevice(IBthDevice device, PhysicalAddress master, byte lsb, byte msb)
        : base(new BthHandle(lsb, msb)) {
      BluetoothDevice = device;
      HostAddress = master;
    }

    #endregion
  }
}