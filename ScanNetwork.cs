using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reactive.Subjects;
using System.Threading.Tasks;

namespace ScanNetwork
{
	public class Scanner
	{
		private readonly int NumberOfWorkers = 40;
		private readonly TimeSpan HeartBeatWaitTime = TimeSpan.FromSeconds(5);

		private ConcurrentQueue<IPAddress> _scanQueue = new ConcurrentQueue<IPAddress>();
		private ConcurrentDictionary<IPAddress, DateTime> _networkDevices = new ConcurrentDictionary<IPAddress, DateTime>();
		private bool _scanning;

		private Subject<IPAddress> _whenDeviceDropped = new Subject<IPAddress>();
		public IObservable<IPAddress> WhenDeviceDropped => _whenDeviceDropped;

		private Subject<IPAddress> _whenDeviceFound = new Subject<IPAddress>();
		public IObservable<IPAddress> WhenDeviceFound => _whenDeviceFound;

		public List<IPAddress> NetworkDevices
		{
			get
			{
				var result = new List<IPAddress>();
				foreach (var device in _networkDevices.ToList())
				{
					result.Add(new IPAddress(device.Key.GetAddressBytes()));
				}
				return result;
			}
		}

		public string NetworkGateway
		{
			get
			{
				return NetworkInterface.GetAllNetworkInterfaces()
								   .Where(x => x.OperationalStatus == OperationalStatus.Up)
										  .Select(y => y.GetIPProperties().GatewayAddresses)
										  .FirstOrDefault()
										  .Select(z => z.Address)
										  .FirstOrDefault()
										  .ToString();
			}
		}

		public void StartScan()
		{
			_networkDevices.Clear();
			PopulateQueue();
			#pragma warning disable CS4014
			StartWorkers();
			#pragma warning restore CS4014
		}

		public void StopScan()
		{
			_scanning = false;
		}

		public async Task Ping(IPAddress ip)
		{
			try
			{
				var ping = new Ping();
				ping.PingCompleted += (sender, e) =>
				{

				};
				var reply = await ping.SendPingAsync(ip, 2000);
				if (reply.Status == IPStatus.Success)
				{
					if (!_networkDevices.ContainsKey(ip))
					{
						_networkDevices.TryAdd(ip, DateTime.Now);
						_whenDeviceFound.OnNext(ip);
					}
					else
					{
						_networkDevices[ip] = DateTime.Now;
					}
				}
				else
				{
					PingFailed(ip);
				}
			}
			catch (Exception)
			{
				PingFailed(ip);
			}
		}

		private void PingFailed(IPAddress ip)
		{
			if (_networkDevices.ContainsKey(ip))
			{
				DateTime ignored;
				_networkDevices.TryRemove(ip, out ignored);
				_whenDeviceDropped.OnNext(ip);
			}
			_scanQueue.Enqueue(ip);
		}

		private void PopulateQueue()
		{
			_scanQueue = new ConcurrentQueue<IPAddress>();
			var splitGateway = NetworkGateway.Split('.');
			for (int i = 1; i <= 255; i++)
			{
				_scanQueue.Enqueue(IPAddress.Parse(splitGateway[0] + "." + splitGateway[1] + "." + splitGateway[2] + "." + i));
			}
		}

		private async Task StartWorkers()
		{
			if (!_scanning)
			{
				await Task.Delay(TimeSpan.FromSeconds(2)); // Allows workers to stop if StopScan() was barely called
			}

			_scanning = true;

			for (int i = 0; i < NumberOfWorkers; i++)
			{
				#pragma warning disable CS4014
				ScanWorker();
				#pragma warning restore CS4014
			}

			#pragma warning disable CS4014
			HeartBeatWorker();
			#pragma warning restore CS4014
		}

		private async Task ScanWorker()
		{
			while (_scanning)
			{
				IPAddress ip;
				if (_scanQueue.TryDequeue(out ip))
				{
					await Ping(ip);
				}
				else
				{
					await Task.Delay(TimeSpan.FromMilliseconds(100));
				}
			}
		}

		private async Task HeartBeatWorker()
		{
			while (_scanning)
			{
				foreach (var ip in _networkDevices)
				{
					if (DateTime.Now - ip.Value >= HeartBeatWaitTime)
					{
						await Ping(ip.Key);
					}
				}
				await Task.Delay(TimeSpan.FromMilliseconds(100));
			}
		}
	}
}
