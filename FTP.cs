
using System.Net;
using System.Net.Sockets;

namespace ConfigTool;

// https://datatracker.ietf.org/doc/html/rfc959

class FTPServer(IPAddress address, ushort port) : TcpListener(address, port) {
	public void Start()
	{
		base.Start();
		Console.Error.WriteLine("Bound ftp server to "+base.LocalEndpoint);
	}

	public async Task<FTPConnection> Accept()
	{
		var con = new FTPConnection(await this.AcceptTcpClientAsync());
		await con.SendAndLog("220 Service ready for new user.");
		return con;
	}
}

class FTPConnection {
	public TcpClient client;
	public NetworkStream commandStream;
	public StreamReader reader;
	public StreamWriter writer;

	public ushort dataPort = 20;
	public bool currentlyTransferring = false;
	public bool shouldCloseAfterTransfer = false;

	public FTPConnection(TcpClient client)
	{
		this.client = client;
		this.commandStream = client.GetStream();
		this.reader = new(this.commandStream, leaveOpen: true);
		this.writer = new(this.commandStream, leaveOpen: true);
	}


	public Task SendFileOnAllPaths(string localPath) => LoopCore(async (splits) => {
		switch(splits[0]) {
			case "RETR":
				//var reqFileName = Path.GetFileName(splits[1]);
				this.currentlyTransferring = true;
				await SendAndLog("150 File status okay. About to open data connection.");
				await SendFile(localPath);
				await SendAndLog("226 Closing data connection. Transfer complete.");
				if(this.shouldCloseAfterTransfer)
					this.CloseStreamSafe();
				this.currentlyTransferring = false;
				return true;

			default:
				return false;
		}
	});

	public Task StoreFileOnAllPaths(string localPathSegment) => LoopCore(async (splits) => {
		switch(splits[0]) {
			case "STOR":
				var reqFileName = Path.GetFileName(splits[1]);
				this.currentlyTransferring = true;
				await SendAndLog("150 File status okay. About to open data connection.");
				await RetrieveFile(Path.Combine(localPathSegment, reqFileName));
				await SendAndLog("226 Closing data connection. Transfer complete.");
				if(this.shouldCloseAfterTransfer)
					this.CloseStreamSafe();
				this.currentlyTransferring = false;
				return true;

			default:
				return false;
		}
	});

	async Task LoopCore(Func<string[], Task<bool>> cmdHandler)
	{
		for(string? line; (line = await reader.ReadLineAsync()) != null; ) {
			Console.Error.WriteLine($"FTP < "+line);
			var splits = line.Split(' ');
			switch(splits[0]) {
				case "AUTH":
					await SendAndLog("500 Not implemented.");
					break;

				case "PWD":
					await SendAndLog("257 /");
					break;

				case "PORT":
					var psplits = splits[1].Split(',');
					this.dataPort = (ushort)((int.Parse(psplits[4]) << 8) | int.Parse(psplits[5]));
					await SendAndLog("200 Okay.");
					break;

				case "QUIT":
					await SendAndLog("221 Service closing control connection.");
					if(this.currentlyTransferring) {
						this.shouldCloseAfterTransfer = true;
					}
					else {
						this.CloseStreamSafe();
					}
					return;

				default:
					if(!await cmdHandler.Invoke(splits)) {
						await SendAndLog("202 Command not implemented, superfluous at this site.");
					}
					break;
			}
		}
	}

	void CloseStreamSafe()
	{
		try {
			commandStream.Close();
		}
		catch(IOException ex) {
			Console.Error.WriteLine($"IOException during QUIT, that's probably fine: "+ex.Message);
		}
	}

	async Task SendFile(string localPath)
	{
		using var dataConnection = new TcpClient();
		var dataEp = new IPEndPoint(((IPEndPoint)client.Client.RemoteEndPoint).Address, this.dataPort);
		await dataConnection.ConnectAsync(dataEp);

		using var file = File.OpenRead(localPath);
		using var stream = dataConnection.GetStream();
		await file.CopyToAsync(stream);
	}

	async Task RetrieveFile(string localPath)
	{
		using var dataConnection = new TcpClient();
		var dataEp = new IPEndPoint(((IPEndPoint)client.Client.RemoteEndPoint).Address, this.dataPort);
		await dataConnection.ConnectAsync(dataEp);

		Directory.CreateDirectory(Path.GetDirectoryName(localPath));
		using var file = File.OpenWrite(localPath);
		using var stream = dataConnection.GetStream();
		await stream.CopyToAsync(file);
	}

	public async Task SendAndLog(string command)
	{
		Console.Error.WriteLine($"FTP > "+command);
		await this.writer.WriteAsync(command);
		await this.writer.WriteAsync("\r\n");
		await this.writer.FlushAsync();
	}
}

