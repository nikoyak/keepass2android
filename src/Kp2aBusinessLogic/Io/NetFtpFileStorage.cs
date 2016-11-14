using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.FtpClient;
using System.Reflection;
using System.Threading;
using Android.Content;
using Android.OS;
using Android.Preferences;
using KeePassLib;
using KeePassLib.Serialization;
using KeePassLib.Utility;

namespace keepass2android.Io
{
	public class NetFtpFileStorage: IFileStorage
	{
		class RetryConnectFtpClient : FtpClient
		{
			protected override FtpClient CloneConnection()
			{
				RetryConnectFtpClient conn = new RetryConnectFtpClient();

				conn.m_isClone = true;

				foreach (PropertyInfo prop in GetType().GetProperties())
				{
					object[] attributes = prop.GetCustomAttributes(typeof(FtpControlConnectionClone), true);

					if (attributes != null && attributes.Length > 0)
					{
						prop.SetValue(conn, prop.GetValue(this, null), null);
					}
				}

				// always accept certficate no matter what because if code execution ever
				// gets here it means the certificate on the control connection object being
				// cloned was already accepted.
				conn.ValidateCertificate += new FtpSslValidation(
					delegate(FtpClient obj, FtpSslValidationEventArgs e)
					{
						e.Accept = true;
					});

				return conn;
			}

			private static T DoInRetryLoop<T>(Func<T> func)
			{
				double timeout = 30.0;
				double timePerRequest = 1.0;
				var startTime = DateTime.Now;
				while (true)
				{
					var attemptStartTime = DateTime.Now;
					try
					{
						return func();
					}
					catch (System.Net.Sockets.SocketException e)
					{
						if ((e.ErrorCode != 10061) || (DateTime.Now > startTime.AddSeconds(timeout)))
						{
							throw;
						}
						double secondsSinceAttemptStart = (DateTime.Now - attemptStartTime).TotalSeconds;
						if (secondsSinceAttemptStart < timePerRequest)
						{
							Thread.Sleep(TimeSpan.FromSeconds(timePerRequest - secondsSinceAttemptStart));
						}
					}
				}
			}
			public override void Connect()
			{
				DoInRetryLoop(() =>
				{
					base.Connect();
					return true;
				}
				);
			}
		}

		public struct ConnectionSettings
		{
			public FtpEncryptionMode EncryptionMode {get; set; }

			public static ConnectionSettings FromIoc(IOConnectionInfo ioc)
			{
				string path = ioc.Path;
				int schemeLength = path.IndexOf("://", StringComparison.Ordinal);
				path = path.Substring(schemeLength + 3);
				string settings = path.Substring(0, path.IndexOf("/", StringComparison.Ordinal));
				return new ConnectionSettings()
				{
					EncryptionMode = (FtpEncryptionMode) int.Parse(settings)
				};

			}

			public string ToString()
			{
				return ((int) EncryptionMode).ToString();
			}
		}

		private readonly Context _context;

		public MemoryStream traceStream;

		public NetFtpFileStorage(Context context)
		{
			_context = context;
			traceStream = new MemoryStream();
			FtpTrace.AddListener(new System.Diagnostics.TextWriterTraceListener(traceStream));
			
		}

		public IEnumerable<string> SupportedProtocols
		{
			get { yield return "ftp"; }
		}

		public void Delete(IOConnectionInfo ioc)
		{
			try
			{
				using (FtpClient client = GetClient(ioc))
				{
					string localPath = IocPathToUri(ioc.Path).PathAndQuery;
					if (client.DirectoryExists(localPath))
						client.DeleteDirectory(localPath, true);
					else
						client.DeleteFile(localPath);
				}
				
			}
			catch (FtpCommandException ex)
				{
					throw ConvertException(ex);
				}
		
	}

		public static Exception ConvertException(Exception exception)
		{
			if (exception is FtpCommandException)
			{
				var ftpEx = (FtpCommandException) exception;

				if (ftpEx.CompletionCode == "550")
					throw new FileNotFoundException(exception.Message, exception);
			}

			return exception;
		}


		internal FtpClient GetClient(IOConnectionInfo ioc, bool enableCloneClient = true)
		{
			FtpClient client = new RetryConnectFtpClient();
			if ((ioc.UserName.Length > 0) || (ioc.Password.Length > 0))
				client.Credentials = new NetworkCredential(ioc.UserName, ioc.Password);
			else
				client.Credentials = new NetworkCredential("anonymous", ""); //TODO TEST

			Uri uri = IocPathToUri(ioc.Path);
			client.Host = uri.Host;
			if (!uri.IsDefaultPort) //TODO test
				client.Port = uri.Port;
			
			//TODO Validate 
			//client.ValidateCertificate += app...

			// we don't need to be thread safe in a classic sense, but OpenRead and OpenWrite don't 
			//perform the actual stream operation so we'd need to wrap the stream (or just enable this:)
			

			client.EncryptionMode = ConnectionSettings.FromIoc(ioc).EncryptionMode;

			
				client.Connect();
				return client;
			
		}


		

		internal Uri IocPathToUri(string path)
		{
			//remove additional stuff like TLS param
			int schemeLength = path.IndexOf("://", StringComparison.Ordinal);
			string scheme = path.Substring(0, schemeLength);
			path = path.Substring(schemeLength + 3);
			string settings = path.Substring(0, path.IndexOf("/", StringComparison.Ordinal));
			path = path.Substring(settings.Length + 1);
			return new Uri(scheme + "://" + path);
		}

		private string IocPathFromUri(IOConnectionInfo baseIoc, Uri uri)
		{
			string basePath = baseIoc.Path;
			int schemeLength = basePath.IndexOf("://", StringComparison.Ordinal);
			string scheme = basePath.Substring(0, schemeLength);
			basePath = basePath.Substring(schemeLength + 3);
			string baseSettings = basePath.Substring(0, basePath.IndexOf("/", StringComparison.Ordinal));
			basePath = basePath.Substring(baseSettings.Length+1);
			string baseHost = basePath.Substring(0, basePath.IndexOf("/", StringComparison.Ordinal));
			return scheme + "://" + baseSettings + "/" + baseHost + uri.AbsolutePath; //TODO does this contain Query?
		}


		public bool CheckForFileChangeFast(IOConnectionInfo ioc, string previousFileVersion)
		{
			return false;
		}

		public string GetCurrentFileVersionFast(IOConnectionInfo ioc)
		{
			return null;
		}

		public Stream OpenFileForRead(IOConnectionInfo ioc)
		{
			try
			{
				using (var cl = GetClient(ioc))
				{
					return cl.OpenRead(IocPathToUri(ioc.Path).PathAndQuery, FtpDataType.Binary, 0);
				}
			}
			catch (FtpCommandException ex)
			{
				throw ConvertException(ex);
			}
		}

		public IWriteTransaction OpenWriteTransaction(IOConnectionInfo ioc, bool useFileTransaction)
		{
			try
			{


				if (!useFileTransaction)
					return new UntransactedWrite(ioc, this);
				else
					return new TransactedWrite(ioc, this);
			}
			catch (FtpCommandException ex)
			{
				throw ConvertException(ex);
			}
		}

		public string GetFilenameWithoutPathAndExt(IOConnectionInfo ioc)
		{
			//TODO does this work when flags are encoded in the iocPath?
			return UrlUtil.StripExtension(
				UrlUtil.GetFileName(ioc.Path));
		}

		public bool RequiresCredentials(IOConnectionInfo ioc)
		{
			return ioc.CredSaveMode != IOCredSaveMode.SaveCred;
		}

		public void CreateDirectory(IOConnectionInfo ioc, string newDirName)
		{
			try
			{
				using (var client = GetClient(ioc))
				{
					client.CreateDirectory(IocPathToUri(GetFilePath(ioc, newDirName).Path).PathAndQuery);
				}
			}
			catch (FtpCommandException ex)
			{
				throw ConvertException(ex);
			}
		}

		public IEnumerable<FileDescription> ListContents(IOConnectionInfo ioc)
		{
			try
			{
				using (var client = GetClient(ioc))
				{
					List<FileDescription> files = new List<FileDescription>();
					foreach (FtpListItem item in client.GetListing(IocPathToUri(ioc.Path).PathAndQuery,
						FtpListOption.Modify | FtpListOption.Size | FtpListOption.DerefLinks))
					{

						switch (item.Type)
						{
							case FtpFileSystemObjectType.Directory:
								files.Add(new FileDescription()
								{
									CanRead = true,
									CanWrite = true,
									DisplayName = item.Name,
									IsDirectory = true,
									LastModified = item.Modified,
									Path = IocPathFromUri(ioc, new Uri(item.FullName))
								});
								break;
							case FtpFileSystemObjectType.File:
								files.Add(new FileDescription()
								{
									CanRead = true,
									CanWrite = true,
									DisplayName = item.Name,
									IsDirectory = false,
									LastModified = item.Modified,
									Path = IocPathFromUri(ioc, new Uri(item.FullName)),
									SizeInBytes = item.Size
								});
								break;

						}
					}
					return files;
				}
			}
			catch (FtpCommandException ex)
			{
				throw ConvertException(ex);
			}
		}

		
		public FileDescription GetFileDescription(IOConnectionInfo ioc)
		{
			try
			{
				//TODO when is this called? 
				//is it very inefficient to connect for each description?

				using (FtpClient client = GetClient(ioc))
				{
					
					var uri = IocPathToUri(ioc.Path);
					string path = uri.PathAndQuery;
					return new FileDescription()
					{
						CanRead = true,
						CanWrite = true,
						Path = ioc.Path,
						LastModified = client.GetModifiedTime(path),
						SizeInBytes = client.GetFileSize(path),
						DisplayName = UrlUtil.GetFileName(path),
						IsDirectory = false
					};
				}
			}
			catch (FtpCommandException ex)
			{
				throw ConvertException(ex);
			}
		}

		public bool RequiresSetup(IOConnectionInfo ioConnection)
		{
			return false;
		}

		public string IocToPath(IOConnectionInfo ioc)
		{
			return ioc.Path;
		}

		public void StartSelectFile(IFileStorageSetupInitiatorActivity activity, bool isForSave, int requestCode, string protocolId)
		{
			throw new NotImplementedException();
		}

		public void PrepareFileUsage(IFileStorageSetupInitiatorActivity activity, IOConnectionInfo ioc, int requestCode,
			bool alwaysReturnSuccess)
		{
			Intent intent = new Intent();
			activity.IocToIntent(intent, ioc);
			activity.OnImmediateResult(requestCode, (int)FileStorageResults.FileUsagePrepared, intent);
		}

		public void PrepareFileUsage(Context ctx, IOConnectionInfo ioc)
		{
			
		}

		public void OnCreate(IFileStorageSetupActivity activity, Bundle savedInstanceState)
		{
			
		}

		public void OnResume(IFileStorageSetupActivity activity)
		{
			
		}

		public void OnStart(IFileStorageSetupActivity activity)
		{
			
		}

		public void OnActivityResult(IFileStorageSetupActivity activity, int requestCode, int resultCode, Intent data)
		{
		
		}

		public string GetDisplayName(IOConnectionInfo ioc)
		{
			var uri = IocPathToUri(ioc.Path);
			return uri.ToString(); //TODO is this good?
		}

		public string CreateFilePath(string parent, string newFilename)
		{
			if (!parent.EndsWith("/"))
				parent += "/";
			return parent + newFilename;
		}

		public IOConnectionInfo GetParentPath(IOConnectionInfo ioc)
		{
			return IoUtil.GetParentPath(ioc);
		}

		public IOConnectionInfo GetFilePath(IOConnectionInfo folderPath, string filename)
		{
			IOConnectionInfo res = folderPath.CloneDeep();
			if (!res.Path.EndsWith("/"))
				res.Path += "/";
			res.Path += filename;
			return res;
		}

		public bool IsPermanentLocation(IOConnectionInfo ioc)
		{
			return true;
		}

		public bool IsReadOnly(IOConnectionInfo ioc, OptionalOut<UiStringKey> reason = null)
		{
			return false;
		}
		public Stream OpenWrite(IOConnectionInfo ioc)
		{
			try
			{
				using (var client = GetClient(ioc))
				{
					return client.OpenWrite(IocPathToUri(ioc.Path).PathAndQuery);

				}
			}
			catch (FtpCommandException ex)
			{
				throw ConvertException(ex);
			}
		}
	}

	public class TransactedWrite : IWriteTransaction
	{
		private readonly IOConnectionInfo _ioc;
		private readonly NetFtpFileStorage _fileStorage;
		private readonly IOConnectionInfo _iocTemp;
		private FtpClient _client;
		private Stream _stream;

		public TransactedWrite(IOConnectionInfo ioc, NetFtpFileStorage fileStorage)
		{
			_ioc = ioc;
			_iocTemp = _ioc.CloneDeep();
			_iocTemp.Path += "." + new PwUuid(true).ToHexString().Substring(0, 6) + ".tmp";

			_fileStorage = fileStorage;
		}

		public void Dispose()
		{
			if (_stream != null)
				_stream.Dispose();
			_stream = null;
		}

		public Stream OpenFile()
		{
			try
			{

				_client = _fileStorage.GetClient(_ioc, false);
				_stream = _client.OpenWrite(_fileStorage.IocPathToUri(_iocTemp.Path).PathAndQuery);
				return _stream;
			}
			catch (FtpCommandException ex)
			{
				throw NetFtpFileStorage.ConvertException(ex);
			}
		}

		public void CommitWrite()
		{
			try
			{
				Android.Util.Log.Debug("NETFTP","connected: " + _client.IsConnected.ToString());
				_stream.Close();
				Android.Util.Log.Debug("NETFTP", "connected: " + _client.IsConnected.ToString());

				//make sure target file does not exist:
				//try
				{
					if (_client.FileExists(_fileStorage.IocPathToUri(_ioc.Path).PathAndQuery))
						_client.DeleteFile(_fileStorage.IocPathToUri(_ioc.Path).PathAndQuery);

				}
				//catch (FtpCommandException)
				{
					//TODO get a new clien? might be stale
				}

				_client.Rename(_fileStorage.IocPathToUri(_iocTemp.Path).PathAndQuery,
					_fileStorage.IocPathToUri(_ioc.Path).PathAndQuery);
				
			}
			catch (FtpCommandException ex)
			{
				throw NetFtpFileStorage.ConvertException(ex);
			}
		}
	}

	public class UntransactedWrite : IWriteTransaction
	{
		private readonly IOConnectionInfo _ioc;
		private readonly NetFtpFileStorage _fileStorage;
		private Stream _stream;

		public UntransactedWrite(IOConnectionInfo ioc, NetFtpFileStorage fileStorage)
		{
			_ioc = ioc;
			_fileStorage = fileStorage;
		}

		public void Dispose()
		{
			if (_stream != null)
				_stream.Dispose();
			_stream = null;
		}

		public Stream OpenFile()
		{
			_stream = _fileStorage.OpenWrite(_ioc);
			return _stream;
		}

		public void CommitWrite()
		{
			_stream.Close();
		}
	}
}