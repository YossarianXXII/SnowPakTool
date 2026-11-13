using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SnowPakTool {

	public class CacheBlockReader : CacheBlockFile {

		public CacheBlockReader ( Stream stream ) : base ( stream ) {
			var count = ReadHeader ();
			FileEntries = new CacheBlockFileFileEntry[count];
			ReadFileEntries ();
			BaseOffset = stream.Position;
			var dataLength = GetDataLength ();
			foreach ( var entry in FileEntries ) {
				GetPayloadOffset ( entry , dataLength );
			}
		}


		public void UnpackAll ( string targetDirectory ) {
			Unpack ( targetDirectory , FileEntries );
		}

		public void Unpack ( string targetDirectory , IReadOnlyCollection<CacheBlockFileFileEntry> entries ) {
			if ( targetDirectory is null ) throw new ArgumentNullException ( nameof ( targetDirectory ) );
			if ( entries is null ) throw new ArgumentNullException ( nameof ( entries ) );
			var root = Path.GetFullPath ( targetDirectory );
			var dataLength = GetDataLength ();
			// Validate the complete selection before creating any output.
			var targets = entries.Select ( item => new {
				Path = ExtractionPath.GetDestination ( root , item.ExternalName ) ,
				Offset = GetPayloadOffset ( item , dataLength ) ,
				item.Size
			} ).ToArray ();
			var i = 0;
			foreach ( var targetFile in targets ) {
				i++;
				Console.Write ( $"\rUnpacking: {i}/{entries.Count}" );
				var targetLocation = targetFile.Path;
				Stream.Position = targetFile.Offset;
				ExtractionPath.RejectReparsePoints ( targetLocation );
				Directory.CreateDirectory ( Path.GetDirectoryName ( targetLocation ) );
				ExtractionPath.RejectReparsePoints ( targetLocation );
				using ( var target = File.Open ( targetLocation , FileMode.CreateNew , FileAccess.Write ) ) {
					Stream.CopyBytesTo ( target , targetFile.Size );
				}
			}
			Console.WriteLine ();
		}


		private int ReadHeader () {
			byte[] bytes;
			try {
				bytes = Stream.ReadByteArray ( Signature.Length );
			}
			catch ( EndOfStreamException error ) {
				throw new InvalidDataException ( "Unknown file header format." , error );
			}
			if ( !bytes.SequenceEqual ( Signature ) ) throw new InvalidDataException ( "Unknown file header format." );
			Stream.ReadMagicInt32 ( 1 );
			Stream.ReadMagicByte ( 1 );
			var count = Stream.ReadInt32 ();
			Stream.ReadMagicInt32 ( 4 );
			Stream.ReadMagicByte ( 1 );
			if ( count < 0 ) throw new InvalidDataException ( $"Invalid cache-block entry count ({count})." );
			// Three table markers and at least a name prefix, offset, size and zero per entry.
			Stream.RequireRemainingBytes ( 3 + 20L * count , "cache-block entry tables" );
			return count;
		}

		private long GetDataLength () {
			return ( Stream.CanSeek ? Stream.Length : long.MaxValue ) - BaseOffset;
		}

		private long GetPayloadOffset ( CacheBlockFileFileEntry entry , long dataLength ) {
			var offset = entry.RelativeOffset;
			var size = entry.Size;
			if ( offset < 0 || size < 0 || offset > dataLength || size > dataLength - offset ) {
				throw new InvalidDataException ( $"Invalid cache-block payload range for '{entry.InternalName}' (offset {offset}, size {size})." );
			}
			return BaseOffset + offset;
		}

		private void ReadFileEntries () {
			ReadNames ();
			Stream.ReadMagicByte ( 1 );
			ReadOffsets ();
			Stream.ReadMagicByte ( 1 );
			ReadSizes ();
			Stream.ReadMagicByte ( 1 );
			ReadZeroes ();
		}

		private void ReadNames () {
			for ( int i = 0; i < FileEntries.Length; i++ ) {
				var name = Stream.ReadLength32String ();
				FileEntries[i] = CacheBlockFileFileEntry.FromInternalName ( name );
			}
		}

		private void ReadOffsets () {
			for ( int i = 0; i < FileEntries.Length; i++ ) {
				FileEntries[i].RelativeOffset = Stream.ReadInt64 ();
			}
		}

		private void ReadSizes () {
			for ( int i = 0; i < FileEntries.Length; i++ ) {
				FileEntries[i].Size = Stream.ReadInt32 ();
			}
		}

		private void ReadZeroes () {
			for ( int i = 0; i < FileEntries.Length; i++ ) {
				var zero = Stream.ReadInt32 ();
				if ( zero != 0 ) throw new InvalidDataException ();
			}
		}

	}

}
