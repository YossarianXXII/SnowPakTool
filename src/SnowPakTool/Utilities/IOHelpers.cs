using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SnowPakTool {

	/// <summary>
	/// Simple helpers for cache_block file IO.
	/// </summary>
	public static class IOHelpers {

		public static char[] InvalidPathChars { get; } = Path.GetInvalidPathChars ();
		public static char[] InvalidNameChars { get; } = Path.GetInvalidFileNameChars ();
		public static char[] Wildcards { get; } = new[] { '*' , '?' };


		public static int ReadInt32 ( this Stream stream ) {
			return ReadValue<int> ( stream );
		}

		public static int[] ReadInt32Array ( this Stream stream , int count ) {
			return ReadValuesArray<int> ( stream , count );
		}

		public static long ReadInt64 ( this Stream stream ) {
			return ReadValue<long> ( stream );
		}

		public static long[] ReadInt64Array ( this Stream stream , int count ) {
			return ReadValuesArray<long> ( stream , count );
		}

		public static byte[] ReadByteArray ( this Stream stream , int count ) {
			if ( count < 0 ) throw new ArgumentOutOfRangeException ( nameof ( count ) );
			stream.RequireRemainingBytes ( count , "byte array" );
			var result = new byte[count];
			ReadFull ( stream , result , 0 , count );
			return result;
		}

		public static string ReadString ( this Stream stream , int bytesCount ) {
			if ( bytesCount < 0 ) throw new ArgumentOutOfRangeException ( nameof ( bytesCount ) );
			stream.RequireRemainingBytes ( bytesCount , "string" );
			var buffer = ArrayPool<byte>.Shared.Rent ( bytesCount );
			try {
				ReadFull ( stream , buffer , 0 , bytesCount );
				return MiscHelpers.Encoding.GetString ( buffer , 0 , bytesCount );
			}
			finally {
				ArrayPool<byte>.Shared.Return ( buffer );
			}
		}

		public static string ReadLength32String ( this Stream stream ) {
			var bytesCount = stream.ReadInt32 ();
			if ( bytesCount < 0 ) throw new InvalidDataException ( $"Invalid length-prefixed string byte count ({bytesCount})." );
			return ReadString ( stream , bytesCount );
		}

		public static string[] ReadLength32StringsArray ( this Stream stream , int count ) {
			if ( count < 0 ) throw new ArgumentOutOfRangeException ( nameof ( count ) );
			stream.RequireRemainingBytes ( (long) count * sizeof ( int ) , "string array length prefixes" );
			var result = new string[count];
			for ( int i = 0; i < count; i++ ) {
				result[i] = ReadLength32String ( stream );
			}
			return result;
		}

		public static void WriteString ( this Stream stream , string value ) {
			var buffer = ArrayPool<byte>.Shared.Rent ( value.Length * 4 );
			try {
				var bytesCount = MiscHelpers.Encoding.GetBytes ( value , 0 , value.Length , buffer , 0 );
				stream.Write ( buffer , 0 , bytesCount );
			}
			finally {
				ArrayPool<byte>.Shared.Return ( buffer );
			}
		}

		public static void WriteLength32String ( this Stream stream , string value ) {
			var buffer = ArrayPool<byte>.Shared.Rent ( 4 + value.Length * 4 );
			try {
				var bytesCount = MiscHelpers.Encoding.GetBytes ( value , 0 , value.Length , buffer , 4 );
				buffer.SetValueAt ( 0 , bytesCount );
				stream.Write ( buffer , 0 , 4 + bytesCount );
			}
			finally {
				ArrayPool<byte>.Shared.Return ( buffer );
			}
		}

		public static T ReadValue<T> ( this Stream stream ) where T : unmanaged {
			return ReadValue<T> ( stream , 0 );
		}

		public static T ReadValue<T> ( this Stream stream , int offset ) where T : unmanaged {
			var size = MiscHelpers.SizeOf<T> ();
			if ( offset < 0 || offset > size ) throw new ArgumentOutOfRangeException ( nameof ( offset ) );
			var buffer = ArrayPool<byte>.Shared.Rent ( size );
			try {
				Array.Clear ( buffer , 0 , offset );
				ReadFull ( stream , buffer , offset , size - offset );
				return buffer.GetValueAt<T> ( 0 );
			}
			finally {
				ArrayPool<byte>.Shared.Return ( buffer );
			}
		}

		public static T[] ReadValuesArray<T> ( this Stream stream , int count ) where T : unmanaged {
			if ( count < 0 ) throw new ArgumentOutOfRangeException ( nameof ( count ) );
			stream.RequireRemainingBytes ( (long) count * MiscHelpers.SizeOf<T> () , "value array" );
			var result = new T[count];
			for ( int i = 0; i < count; i++ ) {
				result[i] = ReadValue<T> ( stream );
			}
			return result;
		}

		internal static void RequireRemainingBytes ( this Stream stream , long count , string structure ) {
			if ( count > 0 && stream.CanSeek && count > stream.Length - stream.Position ) {
				throw new EndOfStreamException ( $"Insufficient bytes for {structure}. Expected at least {count}." );
			}
		}

		private static void ReadFull ( Stream stream , byte[] buffer , int offset , int count ) {
			while ( count > 0 ) {
				var read = stream.Read ( buffer , offset , count );
				if ( read == 0 ) throw new EndOfStreamException ();
				offset += read;
				count -= read;
			}
		}

		public static void WriteValue<T> ( this Stream stream , T value ) where T : unmanaged {
			var size = MiscHelpers.SizeOf<T> ();
			var buffer = ArrayPool<byte>.Shared.Rent ( size );
			try {
				buffer.SetValueAt ( 0 , value );
				stream.Write ( buffer , 0 , size );
			}
			finally {
				ArrayPool<byte>.Shared.Return ( buffer );
			}
		}

		public static void WriteValuesArray<T> ( this Stream stream , T[] values ) where T : unmanaged {
			for ( int i = 0; i < values.Length; i++ ) {
				WriteValue ( stream , values[i] );
			}
		}

		/// <summary>
		/// Passes each chunk to a synchronous callback. The buffer is valid only during
		/// the callback, and the count identifies the valid bytes at the start of the buffer.
		/// </summary>
		public static void ProcessChunked ( this Stream stream , long length , Action<byte[] , int> action ) {
			const int Chunk = 65536;
			if ( length <= 0 ) return;
			var buffer = ArrayPool<byte>.Shared.Rent ( Chunk );
			try {
				while ( length > 0 ) {
					var read = stream.Read ( buffer , 0 , (int) Math.Min ( length , Chunk ) );
					if ( read == 0 ) throw new EndOfStreamException ();
					action ( buffer , read );
					length -= read;
				}
			}
			finally {
				ArrayPool<byte>.Shared.Return ( buffer );
			}
		}

		public static void CopyBytesTo ( this Stream source , Stream target , int length ) {
			ProcessChunked ( source , length , ( buffer , read ) => target.Write ( buffer , 0 , read ) );
		}

		public static InvalidDataException MakeBadMagicException ( long offset , string type , int expected , int actual ) {
			return new InvalidDataException ( $"Unexpected {type} 0x{actual:X} at offset 0x{offset:X} (should be 0x{expected:X})." );
		}

		public static void ReadMagicByte ( this Stream stream , byte expected ) {
			var actual = stream.ReadByte ();
			if ( actual != expected ) throw MakeBadMagicException ( stream.Position - 1 , "byte" , expected , actual );
		}

		public static void ReadMagicInt32 ( this Stream stream , int expected ) {
			var actual = ReadInt32 ( stream );
			if ( actual != expected ) throw MakeBadMagicException ( stream.Position - 1 , "dword" , expected , actual );
		}

		public static string NormalizeDirectory ( string directory ) {
			if ( directory is null ) throw new ArgumentNullException ( nameof ( directory ) );
			directory = Path.GetFullPath ( directory );
			return directory[^1] == '\\' ? directory : directory + '\\';
		}

		public static bool FileExistsOrWildcardDirectoryExists ( string location ) {
			return FileExistsOrWildcardDirectoryExists ( location , out _ , out _ );
		}

		public static bool FileExistsOrWildcardDirectoryExists ( string location , out string directory , out string name ) {
			if ( location is null ) throw new ArgumentNullException ( nameof ( location ) );
			directory = Path.GetDirectoryName ( location );
			name = Path.GetFileName ( location );
			if ( location.IndexOfAny ( Wildcards ) < 0 ) {
				return File.Exists ( location );
			}
			else {
				if ( directory.Length > 0 && !Directory.Exists ( directory ) ) return false;
				if ( name.Length <= 0 ) return false;
				var noWilds = Wildcards.Aggregate ( name , ( a , c ) => a.Replace ( c , '_' ) );
				return noWilds.IndexOfAny ( InvalidNameChars ) < 0;
			}
		}

		public static int ComputeCrc32 ( this Stream stream , long length ) {
			using var hasher = new Crc32Managed ();
			stream.ProcessChunked ( length , ( buffer , read ) => hasher.TransformBlock ( buffer , 0 , read , buffer , 0 ) );
			hasher.TransformFinalBlock ( Array.Empty<byte> () , 0 , 0 );
			return ( hasher.Hash[0] << 24 ) | ( hasher.Hash[1] << 16 ) | ( hasher.Hash[2] << 8 ) | hasher.Hash[3];
		}

		public static IEnumerable<KeyValuePair<string , string>> GetRelativeAndFullNames ( string location , IEnumerable<string> excludedDirectories = null ) {
			location = NormalizeDirectory ( location );
			IEnumerable<string> files;
			if ( excludedDirectories == null ) {
				files = Directory.EnumerateFiles ( location , "*" , SearchOption.AllDirectories );
			}
			else {
				var excluded = new HashSet<string> ( excludedDirectories , StringComparer.OrdinalIgnoreCase );
				files = Directory.EnumerateFiles ( location , "*" ).Concat (
					Directory.EnumerateDirectories ( location )
						.Where ( a => !excluded.Contains ( Path.GetFileName ( a ) ) )
						.SelectMany ( a => Directory.EnumerateFiles ( a , "*" , SearchOption.AllDirectories ) )
				);
			}
			return files.Select ( a => new KeyValuePair<string , string> ( a.Substring ( location.Length ) , a ) );
		}

	}


}
