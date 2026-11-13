using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using SnowPakTool.Zip;

namespace SnowPakTool {

	public static class ZipPakHelper {

		public static string LoadListName => "pak.load_list";
		public static ushort ZipVersion => 0x2D; //default to 4.5/ZIP64 even if it's not used


		/// <remarks>
		/// .NET Core version of <see cref="System.IO.Compression.ZipArchive"/> finally supports real store (0) method
		/// (instead of using deflate as .NET Framework does), although it's single-threaded. The problem is it's still
		/// unclear which files can be compressed, and which must be stored.
		/// </remarks>
		public static void CreatePak ( string sourceDirectory , string pakLocation , IEnumerable<string> excludedDirectories = null , IEnumerable<KeyValuePair<string , string>> additionalFiles = null ) {
			if ( sourceDirectory is null ) throw new ArgumentNullException ( nameof ( sourceDirectory ) );
			if ( pakLocation is null ) throw new ArgumentNullException ( nameof ( pakLocation ) );

			Console.WriteLine ( "Preparing ordered zip file..." );

			sourceDirectory = IOHelpers.NormalizeDirectory ( sourceDirectory );
			if ( !Directory.Exists ( sourceDirectory ) ) throw new IOException ( $"Source directory '{sourceDirectory}' does not exist." );
			if ( !File.Exists ( Path.Combine ( sourceDirectory , LoadListName ) ) ) {
				Console.WriteLine ( $"WARNING: {LoadListName} does not exist." );
			}

			using ( var zipStream = File.Open ( pakLocation , FileMode.CreateNew , FileAccess.ReadWrite , FileShare.Read ) ) {
				Console.WriteLine ( $"Scanning: {sourceDirectory}" );
				var files = IOHelpers.GetRelativeAndFullNames ( sourceDirectory , excludedDirectories )
					.Concat ( additionalFiles ?? Enumerable.Empty<KeyValuePair<string , string>> () )
					.OrderBy ( a => a.Key , PakableFileNameComparer.Instance ) //The list file must be the first file in the archive, stored without compression.
					.ToList ()
					;

				Console.Write ( "Storing files..." );
				using ( var zip = new ZipArchive ( zipStream , ZipArchiveMode.Create ) ) {
					var index = 0;
					foreach ( var file in files ) {
						Console.Write ( $"\rAdding file {index + 1}/{files.Count}" );
						var compress = CanBeCompressed ( index , file.Key );
						var entry = zip.CreateEntry ( file.Key , compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression );
                        entry.LastWriteTime = new DateTimeOffset ( 2000 , 1 , 1 , 0 , 0 , 0 , TimeSpan.Zero );
                        entry.ExternalAttributes = 0;
                        using ( var input = File.OpenRead ( file.Value ) )
                        using ( var output = entry.Open () ) input.CopyTo ( output );
						index++;
					}
				}
			}
			Console.WriteLine ();
			Console.WriteLine ( "Done." );
		}

		public static void ListPak ( string pakLocation , LocalHeaderField[] localHeaderFields , bool sort ) {
			var localHeaderFieldNames = localHeaderFields == null ? null : new HashSet<string> ( localHeaderFields.Select ( a => a.ToString () ) );

			var localHeaders = GetLocalHeaders ( pakLocation );
			if ( sort ) {
				localHeaders = localHeaders.OrderBy ( a => a.Name );
			}

			foreach ( var entry in localHeaders ) {
				PrintFields ( in entry.LocalHeader , localHeaderFieldNames );
				Console.Write ( entry.Name );
				Console.WriteLine ();
			}
		}

		public static bool CanBeCompressed ( int index , string internalName ) {
			// Keep the load list and texture headers stored, but deflate regular assets.
			// The original initial.pak uses both ZIP methods; storing every file made
			// rebuilt packages several times larger than the game archive.
			if ( index == 0 && LoadListName.Equals ( internalName , StringComparison.OrdinalIgnoreCase ) ) return false;
			if ( internalName.EndsWith ( ".pct_header" , StringComparison.OrdinalIgnoreCase ) ) return false;
			return true;
		}

		private static void PrintFields<T> ( in T structure , ISet<string> fieldNames ) {
			var fields = typeof ( T ).GetFields ();
			foreach ( var field in fields ) {
				if ( fieldNames == null || fieldNames.Contains ( field.Name ) ) {
					int size;
					object value;
					if ( field.FieldType.IsEnum ) {
						var type = Enum.GetUnderlyingType ( field.FieldType );
						size = Marshal.SizeOf ( type );
						value = Convert.ChangeType ( field.GetValue ( structure ) , type );
					}
					else {
						size = Marshal.SizeOf ( field.FieldType );
						value = field.GetValue ( structure );
					}
					Console.Write ( $"{{0:X{size * 2}}}|" , value );
				}
			}
		}

		private static IEnumerable<(LocalHeader LocalHeader, string Name, List<ExtensibleExtraZip64> Extras)> GetLocalHeaders ( string pakLocation ) {
			using var zip = File.OpenRead ( pakLocation );
			for ( long i = 0; ; i++ ) {
				var signature = zip.ReadInt32 ();
				if ( signature != LocalHeader.DefaultSignature ) break;
				var header = zip.ReadValue<LocalHeader> ( 4 );
				header.Signature = signature;

				ulong compressedSize = header.CompressedSize;
				var name = zip.ReadString ( header.NameLength );

				List<ExtensibleExtraZip64> extras = null;
				for ( int extraLength = header.ExtraLength; extraLength > 0; ) {
					var extraHeader = zip.ReadValue<ExtensibleExtraHeader> ();
					if ( extraHeader.Id == ExtensibleExtraId.Zip64 ) {
						Console.Write ( $",{(int) extraHeader.Id:X4},{extraHeader.Size:X4}" );
						var eez = new ExtensibleExtraZip64 ();
						if ( header.UncompressedSize == 0xFFFF_FFFF ) {
							eez.UncompressedSize = zip.ReadValue<ulong> ();
						}
						if ( header.CompressedSize == 0xFFFF_FFFF ) {
							eez.CompressedSize = compressedSize = zip.ReadValue<ulong> ();
						}
						extraLength -= MiscHelpers.SizeOf<ExtensibleExtraHeader> () + extraHeader.Size;
					}
					else {
						zip.Position += extraHeader.Size;
					}
				}

				yield return (header, name, extras);

				zip.Position += (long) compressedSize;
			}
		}

	}

}
