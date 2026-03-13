using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using System.Web;

namespace ConfigTool;

static class Program {

	[STAThread]
	static async Task<int> Main(string[] args)
	{
		if(args.Length == 0) {
			PrintUsage();
			return 1;
		}

		switch (args[0]) {
			case "scan":
				g_settings = Settings.Load();
				if(args.Length == 2) {
					await TaskScanTarget(args[1]);
					return 0;
				}
				break;

			case "configure":
				g_settings = Settings.Load();
				if(args.Length == 4) {
					await TaskConfigure(args[1], args[2], args[3]);
					return 0;
				}
				break;

			default:
				Console.Error.WriteLine("Unknown command "+args[0]);
				break;
		}

		PrintUsage();
		return 1;
	}

	static Settings g_settings;

	static void PrintUsage()
	{
		Console.Error.WriteLine("""
		Usage:
			ConfigTool.exe scan <target address>
				Scan target to obtain information about.

			ConfigTool.exe configure <target address> <terminal number> <terminal name>
				Configure target terminal with number and name, as well as information taken from settings.ini.
		""");
	}

	static async Task TaskScanTarget(string target)
	{
		var client = SetupHttpClient(target);

		var info = await ScanTarget(client, target);
		Console.WriteLine($"{info.Model} ({info.ModelString}), Type {info.PhoneType}");
	}

	static async Task<TargetInfo> ScanTarget(HttpClient client, string target)
	{
		var (response, body) = await client.GetWithBackup($"/home_page.html", new CancellationTokenSource(g_settings.Timeout).Token);
		response.EnsureSuccessStatusCode();

		var result  = new TargetInfo();
		result.PhoneType = PhoneType._Unknown;

		var match = new Regex(@"<TITLE>optiPoint(.*)Home Page</TITLE>", RegexOptions.IgnoreCase).Match(body);
		if(match.Success) {
			result.ModelString = match.Groups[1].Value;
			if(result.ModelString.Contains("410")) {
				if(result.ModelString.Contains("ent", StringComparison.OrdinalIgnoreCase))
					result.Model = Model.Optipoint410Entry;
				else if(result.ModelString.Contains("plus", StringComparison.OrdinalIgnoreCase))
					result.Model = Model.Optipoint410EconomyPlus;
				else if(result.ModelString.Contains("eco", StringComparison.OrdinalIgnoreCase))
					result.Model = Model.Optipoint410Economy;
				else if(result.ModelString.Contains("sta", StringComparison.OrdinalIgnoreCase))
					result.Model = Model.Optipoint410Standard;
				else if(result.ModelString.Contains("adv", StringComparison.OrdinalIgnoreCase))
					result.Model = Model.Optipoint410Advance;
			}
			else if(result.ModelString.Contains("420")) {
				if(result.ModelString.Contains("plus", StringComparison.OrdinalIgnoreCase))
					result.Model = Model.Optipoint420Economy;
				else if(result.ModelString.Contains("eco", StringComparison.OrdinalIgnoreCase))
					result.Model = Model.Optipoint420Economy;
				else if(result.ModelString.Contains("sta", StringComparison.OrdinalIgnoreCase))
					result.Model = Model.Optipoint420Standard;
				else if(result.ModelString.Contains("adv", StringComparison.OrdinalIgnoreCase))
					result.Model = Model.Optipoint420Advance;
			}

			result.ApplicationKind = result.ModelString.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? ApplicationKind.SIP : ApplicationKind.HFA;
		}

		var matchType = phoneTypeRegex.Match(body);
		if(matchType.Success) result.PhoneType = (PhoneType)int.Parse(matchType.Groups[1].Value);

		return result;
	}

	static readonly Regex phoneTypeRegex = new(@"<INPUT type=""hidden"" name=""PhoneType"" id=""PhoneType"" VALUE=""(\d+)"">", RegexOptions.IgnoreCase);

	class TargetInfo {
		public Model           Model;
		public ApplicationKind ApplicationKind;
		public string          ModelString;
		public PhoneType       PhoneType;  // <INPUT type="hidden" name="PhoneType" id="PhoneType" VALUE="6">
	}

	enum PhoneType {
		_Unknown       = -1,
		// These match the values from the phone, so we kinda have to go the -1 route for unknown.
		Entry          = 0,
		Economy        = 1,
		EconomyPlus    = 7,
		Standard       = 2,
		Advance        = 3,
		EconomySLK     = 4,
		EconomyPlusSLK = 8,
		StandardSLK    = 5,
		AdvanceSLK     = 6,
	}

	enum Model {
		_Unknown = default,
		
		Optipoint410Entry,
		Optipoint410Economy,
		Optipoint410EconomyPlus,
		Optipoint410Standard,
		Optipoint410Advance, // _not_ advanced.. don't ask me why.
		Optipoint420Economy,
		Optipoint420EconomyPlus,
		Optipoint420Standard,
		Optipoint420Advance,
	}

	enum ApplicationKind {
		_Unknown = default,
		HFA, SIP,
	}

	static async Task TaskConfigure(string target, string terminalNumber, string terminalName)
	{
		var client = SetupHttpClient(target);

		var info = await ScanTarget(client, target);

		if(info.Model == Model._Unknown) {
			Console.Error.WriteLine($"Unknown model '{info.ModelString}'");
			Environment.Exit(1);
		}

		var terminalHostname = "PHONE"+terminalNumber;

		var ftpServer = new FTPServer(IPAddress.Any, 21);
		ftpServer.Start();

		if(info.ApplicationKind == ApplicationKind.HFA) {
			Console.Error.Write("Logging in... "); await ConfigureLoginAdmin(client, "/admin/index.html"); Console.Error.WriteLine("Done. ");
			Console.Error.Write("Backing up enb... "); await ConfigureBackupEnb(client, info, ftpServer); Console.Error.WriteLine("Done. ");
			Console.Error.Write("Uploading new application..."); await ConfigureUploadApplication(client, info, ftpServer); Console.Error.WriteLine("Done. ");

			Console.WriteLine("Wait for the terminal to finish updating, then press enter to continue setting up the device.");
			Console.ReadLine();
					
			info.ApplicationKind = ApplicationKind.SIP;
		}

		if(info.ApplicationKind == ApplicationKind.SIP) {
			Console.Error.Write("Logging in... "); await ConfigureLoginAdmin(client, "/admin/index.html"); Console.Error.WriteLine("Done. ");
			Console.Error.Write("Backing up config... "); await ConfigureBackupConfig(client, info, ftpServer); Console.Error.WriteLine("Done. ");
			Console.Error.Write("Configuring network... "); await ConfigureSetupNetwork(client, target, terminalHostname); Console.Error.WriteLine("Done. ");
			Console.Error.Write("Configuring SIP... "); await ConfigureSetupSip(client, info, terminalNumber, terminalName); Console.Error.WriteLine("Done. ");
			Console.Error.Write("Configuring time and date... "); await ConfigureSetupSNTP(client); Console.Error.WriteLine("Done. ");
			Console.Error.Write("Configuring function keys... "); await ConfigureSetupFunctionKeys(client, info); Console.Error.WriteLine("Done. ");
			if(g_settings.RestartAfterConfig) {
				Console.Error.Write("Requesting device restart... "); await ConfigureRestart(client, info); Console.Error.WriteLine("Submitted. ");
			}
		}
	}

	static async Task ConfigureLoginAdmin(HttpClient client, string requestUrl, string password = "123456")
	{
		var content = new FormUrlEncodedContent([
			new("ReqURL", requestUrl),
			new("Pwd", password),
			new("Login", "Login"),
		]);

		var response = await client.PostWithBackup("/local_admin_login.html/LocalAdminLogin", content);
		response.ValidateNoErrors();
	}

	static async Task ConfigureBackupConfig(HttpClient client, TargetInfo info, FTPServer ftpServer)
	{
		var modelInfix = FormatModelFilenameInfix(info.Model);
		var clientPrefix = client.BaseAddress!.DnsSafeHost;
		var baseName = $"{DateTime.Now:yyMMddHHmmss}-{clientPrefix}-{modelInfix}";

		var content = new FormUrlEncodedContent((info.Model, info.ApplicationKind) switch {
			(Model.Optipoint420Advance, ApplicationKind.HFA) => throw new NotImplementedException(),
			(Model.Optipoint420Advance, ApplicationKind.SIP) or
			(_, ApplicationKind.SIP) =>
				(await CollectInputValuesForUrl(client, "/admin/file_transfer.html"))
				.AddOrReplaceInPostParamsList("FsIPorDNS", g_settings.SelfIp) // @cleanup: detect this instead of config
				//.AddOrReplaceInPostParamsList("DSMPresent", "0")
				//.AddOrReplaceInPostParamsList("PhoneType", info.PhoneType.ToString())
				//.AddOrReplaceInPostParamsList("FTPAccName", "guest")
				//.AddOrReplaceInPostParamsList("Uname", "guest")
				//.AddOrReplaceInPostParamsList("Pwd1", "")
				//.AddOrReplaceInPostParamsList("Pwd2", "")
				//.AddOrReplaceInPostParamsList("FsPath", ".")
				//.AddOrReplaceInPostParamsList("FwFsName", $"{modelInfix}.app")
				//.AddOrReplaceInPostParamsList("NbFsName", $"{modelInfix}-n")
				.AddOrReplaceInPostParamsList("CfgFsName", $"{baseName}.cfg")
				//.AddOrReplaceInPostParamsList("HoldMusicName", "music.moh")
				//.AddOrReplaceInPostParamsList("LogoName", "logo.bmp")
				//.AddOrReplaceInPostParamsList("JavaProgName", "")
				//.AddOrReplaceInPostParamsList("LDAPTempName", "")
				//.AddOrReplaceInPostParamsList("DSMFwName", "")
				.AddOrReplaceInPostParamsList("Action", "eFormOptionXferCfgUp")
				.AddOrReplaceInPostParamsList("Submit", "Submit")
			,
			_ => throw new NotImplementedException(),
		});

		var acceptTask = ftpServer.Accept();

		var triggerTask = (info.Model, info.ApplicationKind) switch {
			(Model.Optipoint420Advance, ApplicationKind.HFA) => throw new NotImplementedException(),
			(Model.Optipoint420Advance, ApplicationKind.SIP) or
			(_, ApplicationKind.SIP) =>
				client.PostWithBackup("/admin/file_transfer.html/FileTransfer", content),
			_ => throw new NotImplementedException(),
		};

		var transferTask = acceptTask.ContinueWith(t => t.Result.StoreFileOnAllPaths("file_transfer/backup"), continuationOptions: TaskContinuationOptions.NotOnFaulted).Unwrap();
		var responseTask = triggerTask.ContinueWith(t => t.Result.ValidateNoErrors(), continuationOptions: TaskContinuationOptions.NotOnFaulted);
		Task.WaitAll(transferTask, responseTask);
	}

	static async Task ConfigureBackupEnb(HttpClient client, TargetInfo info, FTPServer ftpServer)
	{
		var modelInfix = FormatModelFilenameInfix(info.Model);
		var clientPrefix = client.BaseAddress!.DnsSafeHost;
		var now = DateTime.Now;
		var baseName = $"{now:yyMMddHHmmss}-{clientPrefix}-{modelInfix}";
		var baseNameShort = $"{now:yyMMddHHmmss}-{clientPrefix[^7..]}";

		var content = new FormUrlEncodedContent((info.Model, info.ApplicationKind) switch {
			(Model.Optipoint420Advance, ApplicationKind.SIP) => throw new NotImplementedException(), // TODO
			(Model.Optipoint420Advance, ApplicationKind.HFA) or
			(_, ApplicationKind.HFA) =>
				(await CollectInputValuesForUrl(client, "/admin/ftp.html"))
				.AddOrReplaceInPostParamsList("ServerAddr", g_settings.SelfIp) // @cleanup: detect this instead of config
				//.AddOrReplaceInPostParamsList("AccountName", "guest")
				//.AddOrReplaceInPostParamsList("Username", "guest")
				//.AddOrReplaceInPostParamsList("Pwd1", "")
				//.AddOrReplaceInPostParamsList("Pwd2", "")
				//.AddOrReplaceInPostParamsList("AppFileName", $"{modelInfix}.app")
				//.AddOrReplaceInPostParamsList("NetBootFileName", $"{modelInfix}-n")
				//.AddOrReplaceInPostParamsList("DsmFileName", "")
				//.AddOrReplaceInPostParamsList("LdapFileName", "")
				.AddOrReplaceInPostParamsList("EnbFileName", $"{baseNameShort}.enb") // seems to have a max len of 24 for HFA
				.AddOrReplaceInPostParamsList("UseFtpDetails", "eUseFtpDetailsMain")
				.AddOrReplaceInPostParamsList("Action", "eFormOptionExportEnb")
			,
			_ => throw new NotImplementedException(),
		});

		var acceptTask = ftpServer.Accept();

		var triggerTask = (info.Model, info.ApplicationKind) switch {
			(Model.Optipoint420Advance, ApplicationKind.SIP) => throw new NotImplementedException(), // TODO
			(Model.Optipoint420Advance, ApplicationKind.HFA) or
			(_, ApplicationKind.HFA) =>
				client.PostWithBackup("/admin/ftp.html/FileTransfer", content),
			_ => throw new NotImplementedException(),
		};

		var transferTask = acceptTask.ContinueWith(t => t.Result.StoreFileOnAllPaths("file_transfer/backup"), continuationOptions: TaskContinuationOptions.NotOnFaulted).Unwrap();
		var responseTask = triggerTask.ContinueWith(t => t.Result.ValidateNoErrors(), continuationOptions: TaskContinuationOptions.NotOnFaulted);
		Task.WaitAll(transferTask, responseTask);
	}

	static async Task ConfigureUploadApplication(HttpClient client, TargetInfo info, FTPServer ftpServer)
	{
		var defaultFilePattern = FormatApplicationDefaultFilenamePattern(info.Model);

		var files = Directory.GetFiles("file_transfer/applications", defaultFilePattern);
		if(files.Length == 0) {
			Console.Error.WriteLine($"Unable to find suitable application firmware file 'file_transfer/applications/{defaultFilePattern}'.");
			Environment.Exit(6);
		}
		Array.Sort(files, (a, b) => b.CompareTo(a)); // sort descending should hopefully get the highest version first.

		var localFilePath = files[0];
		var filename = Path.GetFileName(files[0]);


		Console.Error.WriteLine($"Selecting {filename}");

		var content = new FormUrlEncodedContent((info.Model, info.ApplicationKind) switch {
			(Model.Optipoint420Advance, ApplicationKind.SIP) => throw new NotImplementedException(), // TODO
			(Model.Optipoint420Advance, ApplicationKind.HFA) or
			(_, ApplicationKind.HFA) =>
				(await CollectInputValuesForUrl(client, "/admin/ftp.html"))
				.AddOrReplaceInPostParamsList("ServerAddr", g_settings.SelfIp) // @cleanup: detect this instead of config
				//.AddOrReplaceInPostParamsList("AccountName", "guest")
				//.AddOrReplaceInPostParamsList("Username", "guest")
				//.AddOrReplaceInPostParamsList("Pwd1", "")
				//.AddOrReplaceInPostParamsList("Pwd2", "")
				.AddOrReplaceInPostParamsList("AppFileName", filename)
				//.AddOrReplaceInPostParamsList("NetBootFileName", $"{modelInfix}-n")
				//.AddOrReplaceInPostParamsList("DsmFileName", "")
				//.AddOrReplaceInPostParamsList("LdapFileName", "")
				//.AddOrReplaceInPostParamsList("EnbFileName", $"{baseNameShort}.enb") // seems to have a max len of 24 for HFA
				.AddOrReplaceInPostParamsList("UseFtpDetails", "eUseFtpDetailsMain")
				.AddOrReplaceInPostParamsList("Action", "eFormOptionDownloadApp")
			,
			_ => throw new NotImplementedException(),
		});

		var acceptTask = ftpServer.Accept();

		var triggerTask = (info.Model, info.ApplicationKind) switch {
			(Model.Optipoint420Advance, ApplicationKind.SIP) => throw new NotImplementedException(), // TODO
			(Model.Optipoint420Advance, ApplicationKind.HFA) or
			(_, ApplicationKind.HFA) =>
				client.PostWithBackup("/admin/ftp.html/FileTransfer", content),
			_ => throw new NotImplementedException(),
		};

		var transferTask = acceptTask.ContinueWith(async t => {
			var con = t.Result;
			for(int i = 0; ; i++) {
				try {
					await con.SendFileOnAllPaths(localFilePath);
					break;
				}
				catch(IOException ex)  when (ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase) && i < 3) {
					Console.Error.WriteLine($"Socket closed. The terminal might be restarting to free up memory. Will try to restart the connection (attempt {i + 1} / 3)...");
					con.client.Close();
					con = await ftpServer.Accept();
					continue;
				}
			}
		}, continuationOptions: TaskContinuationOptions.NotOnFaulted);
		
		var responseTask = triggerTask.ContinueWith(t => t.Result.ValidateNoErrors(), continuationOptions: TaskContinuationOptions.NotOnFaulted);

		Task.WaitAll(transferTask, responseTask);
	}

	static string FormatModelFilenameInfix(Model model) => model switch {
		Model.Optipoint410Entry        => "opti-410-ent",
		Model.Optipoint410Economy      => "opti-410-eco",
		Model.Optipoint410EconomyPlus  => "opti-410-ecp",
		Model.Optipoint410Standard     => "opti-410-std",
		Model.Optipoint410Advance      => "opti-410-adv",

		Model.Optipoint420Economy      => "opti-420-eco",
		Model.Optipoint420EconomyPlus  => "opti-420-ecp",
		Model.Optipoint420Standard     => "opti-420-std",
		Model.Optipoint420Advance      => "opti-420-adv",
		
		_ => "opti-unknown",
	};

	static string FormatApplicationDefaultFilenamePattern(Model model) => model switch {
		Model.Optipoint410Entry        => "oP410ent_V*.app",
		Model.Optipoint410Economy      => "oP410eco_V*.app",
		Model.Optipoint410EconomyPlus  => "oP410ecoP_V*.app",
		Model.Optipoint410Standard     => "oP410std_V*.app",
		Model.Optipoint410Advance      => "oP410adv_V*.app",
		
		Model.Optipoint420Economy      => "oP420eco_V*.app",
		Model.Optipoint420EconomyPlus  => "oP420ecoP_V*.app",
		Model.Optipoint420Standard     => "oP420std_V*.app",
		Model.Optipoint420Advance      => "oP420adv_V*.app",

		_ => "oPUnknown",
	};


	static async Task ConfigureSetupNetwork(HttpClient client, string selfAddress, string hostname)
	{
		var content = new FormUrlEncodedContent([
			new("IPAddr", selfAddress),
			new("SMask", g_settings.NetworkMask),
			new("DnsIpAddr", g_settings.Dns1),
			new("DnsSecIpAddr", g_settings.Dns2),
			new("DefRoute", g_settings.DefaultRoute),
			new("DomainName", ""),
			new("UseE164Hostname", "on"),
			new("HostName", hostname),
			new("Route1", "0.0.0.0"),
			new("Route2", "0.0.0.0"),
			new("Gway1", "0.0.0.0"),
			new("Gway2", "0.0.0.0"),
			new("Mask1", "0.0.0.0"),
			new("Mask2", "0.0.0.0"),
			new("NATka", "0"),
		]);

		var response = await client.PostWithBackup("/admin/ip.html/IPAddrRout", content);
		response.ValidateNoErrors();
	}

	static async Task ConfigureSetupSip(HttpClient client, TargetInfo info,  string terminalNumber, string terminalName)
	{
		var content = new FormUrlEncodedContent([
			new("PhoneType", info.PhoneType.ToString("d")),
			new("TermNum", terminalNumber),
			new("TermName", terminalName),
			new("DisplayId", $"{terminalNumber} - {terminalName}"),
			new("UseDisplayId", "on"),
			new("SIPRoutingType", "eFormOptionServer"),
			new("RegistrarAddr", g_settings.SipRegistrar),
			new("RegistrarPort", g_settings.SipRegistrarPort.ToString()),
			new("RegAddr", g_settings.SipServer),
			new("RegPort", g_settings.SipServerPort.ToString()),
			new("gatewayAddr", g_settings.SipGateway),
			new("gatewayPort", g_settings.SipGatewayPort.ToString()),
			new("SIPPort", "5060"),
			new("RTPPort", "5004"),
			new("DefDomain", ""),
			new("SIPTransport", "eFormOptionUDP"),
			new("SIPServerType", "eFormOptionAut"),
			new("SIPSessTimerVal", "3600"),
			new("RegTimerVal", "3600"),
			new("SIPRealm", g_settings.SipDomain),
			new("SIPId", ""),
			new("SIPPwd1", ""),
			new("SIPPwd2", ""),
			new("TransTimVal", "32000"),
			new("RegBackoffTimerVal", "60"),
			new("MWIAddr", ""),
			new("EmerNum", ""),
			new("VoiceMailNum", ""),
			new("Branding", g_settings.Branding),
			new("Submit2", "Submit"),
		]);

		var response = await client.PostWithBackup("/admin/sip_environment.html/SIPEnvironment", content);
		response.ValidateNoErrors();
	}

	static async Task ConfigureSetupSNTP(HttpClient client)
	{
		var data = await CollectInputValuesForUrl(client, "/admin/time.html");
		data.AddOrReplaceInPostParamsList("SNTPServAddr", g_settings.SNTPServer);
		data.AddOrReplaceInPostParamsList("TZOffset", g_settings.TimezoneOffset);
		//data.AddOrReplaceInPostParamsList("Hours", "4");
		//data.AddOrReplaceInPostParamsList("Mins", "58");
		//data.AddOrReplaceInPostParamsList("HoursServed", "4");
		//data.AddOrReplaceInPostParamsList("MinsServed", "58");
		//data.AddOrReplaceInPostParamsList("Day", "11");
		//data.AddOrReplaceInPostParamsList("Month", "eFormOptionMnthMar");
		//data.AddOrReplaceInPostParamsList("Year", "2026");
		//data.AddOrReplaceInPostParamsList("DayServed", "11");
		//data.AddOrReplaceInPostParamsList("MonthServed", "3");
		//data.AddOrReplaceInPostParamsList("YearServed", "2026");
		data.AddOrReplaceInPostParamsList("DateFormat", "eFormOptionDDMMYY");

		if(g_settings.DaylightSavings) data.AddOrReplaceInPostParamsList("Daylight", "on");
		else                           data.RemoveFromPostParamsList("Daylight");
		var content = new FormUrlEncodedContent(data);

		var response = await client.PostWithBackup("/admin/time.html/Time", content);
		response.ValidateNoErrors();
	}

	static async Task ConfigureSetupFunctionKeys(HttpClient client, TargetInfo info)
	{
		var (response, body) = await client.GetWithBackup("/admin/function_keys.html");
		response.EnsureSuccessStatusCode();
		
		if(info.PhoneType == PhoneType._Unknown) {
			var matchType = phoneTypeRegex.Match(body);
			if(matchType.Success) info.PhoneType = (PhoneType)int.Parse(matchType.Groups[1].Value);
		}
		if(info.PhoneType == PhoneType._Unknown) {
			Console.Error.WriteLine("Failed to determine phone type, cannot program keys.");
			return;
		}

		
		int keyCount = info.PhoneType switch { // might only be true for 410s
			PhoneType.Entry      => 8,
			PhoneType.AdvanceSLK => 8 + 4 + 6,
			PhoneType.Advance    => 8 + 4 + 6 + 1,
			_                    => 8 + 4,
		};
		const int MAX_KEY_COUNT = 19;

		var currentKeyFunctions = new FunctionKeyCode[keyCount];

		var currentFunctionRegex = new Regex(@"""Key(\d+)FnNo""\sVALUE=""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
		foreach(Match match in currentFunctionRegex.Matches(body)) {
			var key = int.Parse(match.Groups[1].Value) - 1;
			var currentFunction = (FunctionKeyCode)int.Parse(match.Groups[2].Value);

			if(key >= keyCount) {
				if(key >= MAX_KEY_COUNT && key < MAX_KEY_COUNT * 2) continue; // shift keys ignored for now.

				if(currentFunction != FunctionKeyCode.ClearDefinition) {
					Console.Error.WriteLine($"Key {key + 1} has a function assigned to it ({currentFunction}) even though this model should not even have that key.");
					Environment.Exit(8);
				}

				continue; // the keys are always there, just hidden for smaller phones.
			}


			currentKeyFunctions[key] = currentFunction;
		}

		int leftRowCount = info.Model switch {
			Model.Optipoint410Entry or 
			Model.Optipoint410Economy or 
			Model.Optipoint410EconomyPlus or 
			Model.Optipoint410Standard or 
			Model.Optipoint410Advance => 4,

			Model.Optipoint420Economy or 
			Model.Optipoint420EconomyPlus or 
			Model.Optipoint420Standard or 
			Model.Optipoint420Advance => 5,

			_ => throw new UnreachableException(),
		};

		int rightRowCount = keyCount - leftRowCount;


		var linearizedKeyConfigs = new KeyConfig[keyCount];
		foreach(var config in g_settings.RawKeyConfigs) {
			var rows = config.PhysColumn switch {
				KeyColumn.Left => leftRowCount,
				KeyColumn.Right => rightRowCount,
				_ => throw new UnreachableException(),
			};

			var row = config.PhysRow.GetOffset(rows);
			if(row >= rows || row < 0) {
				Console.Error.WriteLine($"Cannot program key {config.PhysColumn} {config.PhysRow} because this phone doesn't have such a key.");
				continue;
			}

			int key = row;
			if(config.PhysColumn == KeyColumn.Right) key += leftRowCount;

			var config2 = config;
			config2.KeyNum = key + 1;

			linearizedKeyConfigs[key] = config2;
		}


		var first = true;
		foreach(var config in linearizedKeyConfigs) {
			if(config.PhysColumn == KeyColumn._Unknown) continue;

			var currentFunction = currentKeyFunctions[config.KeyNum - 1];
			if(currentFunction == FunctionKeyCode.ClearDefinition && config.Function == FunctionKeyCode.ClearDefinition) continue;

			if(first) {
				Console.Error.WriteLine();
				first = false;
			}

			Console.Error.Write($"Updating key {config.PhysColumn} #{config.PhysRow} -> {config.Function}... ");
			await ConfigureKey(client, info, currentFunction, config);
			Console.Error.WriteLine("Done.");
		}


		static async Task ConfigureKey(HttpClient client, TargetInfo info, FunctionKeyCode currentFunction, KeyConfig config)
		{
			var deleteLineKey = config.Function switch {
				FunctionKeyCode.Line or FunctionKeyCode.DSS => config.Function != currentFunction,
				_ => false,
			};

			string dialString;
			Console.WriteLine(HttpUtility.UrlEncode(config.DisplayString, System.Text.Encoding.Latin1));
			dialString = config.Function switch {
				FunctionKeyCode.SelectedDialing => 
					config.DialString
					//NOTE(Rennorb): Taken from the js:
					// Used to search through parameter strings and replace any '#' characters with an escape character (%23) as EmWeb does not allow '#' in a string.
					.Replace("#", "%23")
					// Also replace any + with its ASCII code 0x2B (&#43 in HTML)
					.Replace("+", "%2B")
					+
					(info.PhoneType switch {
						PhoneType.EconomySLK or
						PhoneType.StandardSLK or
						PhoneType.AdvanceSLK => $"^{HttpUtility.UrlEncode(config.DisplayString, System.Text.Encoding.Latin1)}",

						_ => "",
					})
				,
				_ => info.PhoneType switch {
					PhoneType.EconomySLK or
					PhoneType.StandardSLK or
					PhoneType.AdvanceSLK => "%3Cno%20label%3E",

					_ => "empty",
				},
			};
		
			//NOTE(Rennorb): Taken from the js:
			//MR H39117 MKE Now we send the appropriate value in the array of current fnno
			string args = $"?0,{config.KeyNum},{(config.Shifted ? '1' : '0')},{config.Function:D},{currentFunction:D},{deleteLineKey},{(config.AdminLocked? '1' : '0')},{dialString}";  // 1st '0' indicates 'Phone'

			var response = await client.GetWithBackup(new Uri(client.BaseAddress!, "/admin/save_definitions_popup.html"+args, true));
			response.ValidateNoErrors();
		}
	}

	static async Task ConfigureRestart(HttpClient client, TargetInfo info)
	{
		var content = new FormUrlEncodedContent([
			(info.Model, info.ApplicationKind) switch {
				(Model.Optipoint420Advance, ApplicationKind.HFA) => new("Dummy", "Restart"),
				_ => new("Submit", "Restart"),
			},
		]);

		var response = await client.PostWithBackup("/admin/restart.html/Restart", content);
		response.ValidateNoErrors();
	}


	static HttpClient SetupHttpClient(string target)
	{
		var handler = new HttpClientHandler();
		handler.ClientCertificateOptions = ClientCertificateOption.Manual;
		handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
#pragma warning disable SYSLIB0039 // Type or member is obsolete
		handler.SslProtocols |= SslProtocols.Tls /* | SslProtocols.Tls11 | SslProtocols.Tls12*/;
#pragma warning restore SYSLIB0039 // Type or member is obsolete

		handler.UseCookies = true;

		if(!string.IsNullOrEmpty(g_settings.HTTPProxy)) {
			Console.Error.WriteLine("Using HTTP Proxy "+g_settings.HTTPProxy);

			handler.Proxy = new WebProxy(new Uri(g_settings.HTTPProxy));
			handler.UseProxy = true;
		}

		var client = new HttpClient(handler);
		client.BaseAddress = new Uri($"https://{target}");
		return client;
	}

	static Task<(HttpResponseMessage, string)> GetWithBackup(this HttpClient client, string path, CancellationToken ct = default)
	{
		return GetWithBackup(client, new Uri(client.BaseAddress!, path), ct);
	}

	static async Task<(HttpResponseMessage, string)> GetWithBackup(this HttpClient client, Uri path, CancellationToken ct = default)
	{
		HttpResponseMessage response;
		string body;
		for(int backoff = 200; true; backoff *= 2) {
			response = await client.GetAsync(path, ct);
			body = await response.Content.ReadAsStringAsync(ct);

			if(response.StatusCode != HttpStatusCode.MethodNotAllowed && !body.Contains("LocalAdminLogin", StringComparison.OrdinalIgnoreCase)) break;

			if(backoff < 2000) {
				if(response.StatusCode != HttpStatusCode.MethodNotAllowed)
					Console.Error.WriteLine("Received 405 MethodNotAllowed, will retry after a short delay... ");
				else
					Console.Error.WriteLine("Seem to have been redirected to the login page, will retry after a short delay... ");
				await Task.Delay(backoff, ct);
			}
			else {
				Console.Error.WriteLine("This doesn't seem to work, will reauthenticate... ");

				var inputs = CollectInputValues(body);

				await ConfigureLoginAdmin(client, inputs.FirstOrDefault(p => p.Key == "ReqURL").Value ?? "/admin/index.html");
				backoff = 200;
			}
		}

		return (response, body);
	}

	static async Task<(HttpResponseMessage, string)> PostWithBackup(this HttpClient client, string path, HttpContent content, CancellationToken ct = default)
	{
		HttpResponseMessage response;
		string body;
		for(int backoff = 200; true; backoff *= 2) {
			response = await client.PostAsync(path, content, ct);
			body = await response.Content.ReadAsStringAsync(ct);

			if(response.StatusCode != HttpStatusCode.MethodNotAllowed && !body.Contains("LocalAdminLogin", StringComparison.OrdinalIgnoreCase)) break;
			
			if(backoff < 2000) {
				if(response.StatusCode != HttpStatusCode.MethodNotAllowed)
					Console.Error.WriteLine("Received 405 MethodNotAllowed, will retry after a short delay... ");
				else
					Console.Error.WriteLine("Seem to have been redirected to the login page, will retry after a short delay... ");
				await Task.Delay(backoff, ct);
			}
			else {
				Console.Error.WriteLine("This doesn't seem to work, will reauthenticate... ");

				var inputs = CollectInputValues(body);

				await ConfigureLoginAdmin(client, inputs.FirstOrDefault(p => p.Key == "ReqURL").Value ?? "/admin/index.html");
				backoff = 200;
			}
		}

		return (response, body);
	}

	static async Task ValidateNoErrors(this HttpResponseMessage response, string? body = null)
	{
		body ??= await response.Content.ReadAsStringAsync();
		ValidateNoErrors((response, body));
	}
	static void ValidateNoErrors(this (HttpResponseMessage response, string body) tpl)
	{
		tpl.response.EnsureSuccessStatusCode();
		
		var errorRegex = new Regex(@"<script>\s*alert\(\s*""(.*?)""\s*\)", RegexOptions.IgnoreCase).Match(tpl.body);
		if(errorRegex.Success) {
			Console.Error.Write("Transfer error: ");
			Console.Error.WriteLine(errorRegex.Groups[1].Value.Replace("\\\"", "\""));

			Environment.Exit(4);
		}
	}

	static async Task<List<KeyValuePair<string, string>>> CollectInputValuesForUrl(HttpClient client, string path, CancellationToken ct = default)
	{
		var (response, body) = await GetWithBackup(client, path, ct);
		response.EnsureSuccessStatusCode();

		return CollectInputValues(body);
	}

	static List<KeyValuePair<string, string>> CollectInputValues(string html)
	{
		var result = new List<KeyValuePair<string, string>>();

		// <INPUT name="ServerAddr" SIZE="24" VALUE="192.168.102.31">
		// <INPUT name="Pwd2" type="Password" SIZE="24" VALUE="">
		// <INPUT type="radio" name="UseFtpDetails" value="eUseFtpDetailsMain" CHECKED>
		// <INPUT type="radio" name="UseFtpDetails" value="eUseFtpDetailsLdap" >

		var inputsRegex = new Regex(@"<input (.*?name=""(.*?)"".*?)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
		var valueRegex = new Regex(@"value=""(.*?)""", RegexOptions.IgnoreCase);
		foreach (Match match in inputsRegex.Matches(html)) {
			var attrs = match.Groups[1].Value;
			var name = match.Groups[2].Value;

			if(attrs.Contains("radio", StringComparison.OrdinalIgnoreCase) || attrs.Contains("checkbox", StringComparison.OrdinalIgnoreCase)) {
				if(!attrs.Contains("checked", StringComparison.OrdinalIgnoreCase)) continue;
			}
			if(attrs.Contains(@"type=""submit""", StringComparison.OrdinalIgnoreCase)) continue;
			
			var valueMatch = valueRegex.Match(attrs);
			if(valueMatch.Success) {
				result.Add(new(name, valueMatch.Groups[1].Value));
			}
		}

		var selectsRegex = new Regex(@"<select .*?name=""(.*?)"".*?>(.*?)</select>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
		var optionsRegex = new Regex(@"<option (.*?value=(?:""(?<2>.*?)""|(?<2>.*?) ).*?)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
		foreach (Match match in selectsRegex.Matches(html)) {
			var name = match.Groups[1].Value;
			var children = match.Groups[2].Value;

			foreach(Match optionMatch in optionsRegex.Matches(children)) {
				var attrs = optionMatch.Groups[1].Value;
				
				if(attrs.Contains(@"selected", StringComparison.OrdinalIgnoreCase)) {
					result.Add(new(name, optionMatch.Groups[2].Value));
					goto break2; // no "multiple" support for now
				}
			}
		}
		break2:;

		return result;
	}

	static List<KeyValuePair<string, string>> AddOrReplaceInPostParamsList(this List<KeyValuePair<string, string>> list, string key, string value)
	{
		var didReplace = false;
		for(int i = 0; i < list.Count; i++) {
			if(list[i].Key == key) {
				if(!didReplace) {
					list[i] = new(key, value);
					didReplace = true;
				}
				else {
					list.RemoveAt(i);
				}
			}
		}

		if(!didReplace) list.Add(new(key, value));

		return list;
	}

	static List<KeyValuePair<string, string>> RemoveFromPostParamsList(this List<KeyValuePair<string, string>> list, string key)
	{
		for(int i = 0; i < list.Count; i++) {
			if(list[i].Key == key) {
				list.RemoveAt(i);
			}
		}

		return list;
	}
}

struct Settings
{
	public string HTTPProxy;
	public bool   RestartAfterConfig;

	public int    Timeout;
	public int    RequestDelay;
	public string SelfIp;

	public string NetworkMask;
	public string DefaultRoute;
	public string Dns1;
	public string Dns2;
	
	public string SipRegistrar;
	public ushort SipRegistrarPort;
	public string SipServer;
	public ushort SipServerPort;
	public string SipGateway;
	public ushort SipGatewayPort;
	public string SipDomain;
	public string Branding;
	
	public string SNTPServer;
	public string TimezoneOffset;
	public bool   DaylightSavings;

	public List<KeyConfig> RawKeyConfigs;

	public static Settings Load()
	{
		var settings = new Settings();
		settings.RawKeyConfigs = new();

		using var file = File.OpenRead("settings.ini");
		using var reader = new StreamReader(file, System.Text.Encoding.UTF8);
		for(string? line; (line = reader.ReadLine()) != null; ) {
			line = line.Trim();
			if(string.IsNullOrEmpty(line) || line.StartsWith("[") || line.StartsWith("#")) continue;

			var li = line.IndexOf('#');
			if(li > -1) line = line[..li];

			var kv = line.Split('=', 2);
			if(kv.Length != 2) continue;

			var value = kv[1].Trim();

			var settingsKey = kv[0].Trim();
			switch(settingsKey) {
				case "HTTPProxy": settings.HTTPProxy = value; break;
				case "RequestDelay": settings.RequestDelay = int.Parse(value); break;
				case "RestartAfterConfig": settings.RestartAfterConfig = bool.Parse(value); break;

				case "Timeout": settings.Timeout = int.Parse(value); break;
				case "SelfIp": settings.SelfIp = value; break;
				
				case "NetworkMask": settings.NetworkMask = value; break;
				case "DefaultRoute": settings.DefaultRoute = value; break;
				case "Dns1": settings.Dns1 = value; break;
				case "Dns2": settings.Dns2 = value; break;
				
				case "SipRegistrar": settings.SipRegistrar = value; break;
				case "SipRegistrarPort": settings.SipRegistrarPort = ushort.Parse(value); break;

				case "SipServer": settings.SipServer = value; break;
				case "SipServerPort": settings.SipServerPort = ushort.Parse(value); break;
				
				case "SipGateway": settings.SipGateway = value; break;
				case "SipGatewayPort": settings.SipGatewayPort = ushort.Parse(value); break;

				case "SipDomain": settings.SipDomain = value; break;

				case "Branding": settings.Branding = value; break;

				case "SNTPServer": settings.SNTPServer = value; break;
				case "TimezoneOffset": settings.TimezoneOffset = value; break;
				case "DaylightSavings": settings.DaylightSavings = bool.Parse(value); break;

				default:
					if(settingsKey.StartsWith("Key")) {
						var keyConfig = new KeyConfig();
						switch(settingsKey[3]) {
							case 'R': keyConfig.PhysColumn = KeyColumn.Right; break;
							case 'L': keyConfig.PhysColumn = KeyColumn.Left; break;
							default:
								Console.Error.WriteLine($"Failed to parse key definition: {settingsKey[3]} is neither R nor L.");
								Environment.Exit(1); break;
						}
						var physRow = settingsKey[4..];
						keyConfig.PhysRow = physRow.StartsWith('-') ? new Index(int.Parse(physRow[1..]), true) : new Index(int.Parse(physRow) -1);

						var vSplits = value.Split(',', StringSplitOptions.TrimEntries);
						if(!Enum.TryParse(vSplits[0], out keyConfig.Function)) {
							Console.Error.WriteLine($"Failed to parse key definition: {vSplits[0]} is not a valid function.");
							Environment.Exit(1);
						}

						switch(keyConfig.Function) {
							case FunctionKeyCode.SelectedDialing:
								if(vSplits.Length == 0) {
									Console.Error.WriteLine($"Failed to parse key definition: {vSplits[0]} is not a valid function.");
									Environment.Exit(1);
								}
								if(vSplits.Length >= 1) keyConfig.DialString = vSplits[1];
								if(vSplits.Length >= 2) keyConfig.DisplayString = vSplits[2];
								break;
						}

						settings.RawKeyConfigs.Add(keyConfig);
					}
					break;
			}
		}

		return settings;
	}
}

struct KeyConfig {
	public KeyColumn       PhysColumn;
	public Index           PhysRow;
	public int             KeyNum;
	public bool            Shifted;
	public bool            AdminLocked;
	public FunctionKeyCode Function;
	public string          DialString;
	public string          DisplayString;
}

enum KeyColumn { _Unknown = default, Left, Right }

enum FunctionKeyCode {
	ClearDefinition    =  0,
	SelectedDialing    =  1,
	AbbreviatedDialing =  2,
	RepeatDialing      =  3,
	MissedCalls        =  4,
	VoiceMessages      =  5,
	Forwarding         =  6,
	Loudspeaker        =  7,
	Mute               =  8,
	RingerOff          =  9,
	Hold               = 10,
	Alternate          = 11,
	BlindTransfer      = 12,
	Join               = 13,
	Deflect            = 14,
	SetupMenu          = 15,
	RoomEchoing        = 16,
	RoomMuffled        = 17,
	Shift              = 18,
	Notebook           = 19,
	Settings           = 20,
	PhoneLock          = 21,
	Conference         = 22,
	LocalConference    = 23,
	Headset            = 24,
	DoNotDisturb       = 25,
	Status             = 26,
	Contacts           = 27,
	InstantMsg         = 28,
	GroupPickup        = 29,
	RepertoryDial      = 30,
	Line               = 31,
	FeatureToggle      = 32,
	CallPark           = 33,
	CallPickup         = 34,
	SwapScreens        = 35,
	Cancel_Release     = 36,
	Confirm            = 37,
	DSS                = 38,
	Consult_Transfer   = 39,
	Callback           = 40,
	CancelCallbacks    = 41,
	StateKey           = 42,
	Mobility           = 43,
	CallRecording      = 44,
	AICSZipTone        = 45,
}
