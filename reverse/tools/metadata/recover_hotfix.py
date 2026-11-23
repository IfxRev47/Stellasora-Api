"""Reconstruct the Hotfix assembly from the verified StellaSora build."""
import argparse
import hashlib
from pathlib import Path
from unicorn import Uc,UC_ARCH_ARM64,UC_MODE_ARM
from unicorn.arm64_const import *
import struct
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("container", type=Path)
parser.add_argument("image", type=Path, help="matching repaired ARM64 libil2cpp image")
parser.add_argument("output", type=Path)
args = parser.parse_args()
d = bytearray(args.container.read_bytes())
if hashlib.sha256(d).hexdigest() != "30d1a740a55453a816b8b1a65dc8af46ca5483aa4c1d3c4522a8199e0a09e284":
    parser.error("this recovery uses offsets for a different Hotfix build")
f = args.image.read_bytes()
u=Uc(UC_ARCH_ARM64,UC_MODE_ARM)
for addr,size in [(0x3996000,0x3000),(0x2015000,0x2000),(0x3933000,0x2000)]:
 u.mem_map(addr,size);u.mem_write(addr,f[addr:addr+size])
base=0x20000000
u.mem_map(base,0x2000000);u.mem_write(base,bytes(d));u.reg_write(UC_ARM64_REG_SP,base+0x1ff0000)
p=0x110;prog=[]
for i in range(8):
 n,=struct.unpack_from('<I',d,p);prog.append((p+4,n));p=(p+n+7)&~3
def transform(progaddr,plen,off,size,block):
 for reg,v in zip([UC_ARM64_REG_X0,UC_ARM64_REG_X1,UC_ARM64_REG_X2,UC_ARM64_REG_X3,UC_ARM64_REG_X4,UC_ARM64_REG_X5,UC_ARM64_REG_LR],[base+progaddr,plen,base+16,base+off,size,block,base-4]):
  u.reg_write(reg,v)
 u.emu_start(0x3933fa0,base-4)
 return bytes(u.mem_read(base+off,size))
u.mem_write(base+0x1800000,bytes(reversed(range(256))))
if transform(0x1800000, 256, 0x460, 16, 16) != b"Hello, HybridCLR":
    parser.error("the supplied libil2cpp image does not match this container")
streams=[(0x7f940c,0x5d8af4),(0xdd1f00,0x111924),(0xee3824,0xfb2c8),(0xfdeaec,16),(0xfdeafc,0x10aacc)]

for i,(off,n) in zip([5,1,3,None,2],streams):
 if i is not None:
  d[off:off+n]=transform(*prog[i],off,n,256)
o=streams[0][0]
mask=struct.unpack_from('<Q',d,o+8)[0]; t=o+24
counts={}
for i in range(64):
 if mask>>i&1:
  counts[i]=struct.unpack_from('<I',d,t)[0];t+=4
# heap flags = 5, strings and blobs have 4 byte indexes, GUID has 2.
type_start=t+counts[0]*12+counts[1]*10
for offset in range(type_start,type_start+counts[2]*22,22):
 d[offset:offset+22]=transform(*prog[6],offset,22,22)
methods=type_start+counts[2]*22+counts[4]*10
used=set()
for i in range(counts[6]):
 rva=struct.unpack_from('<I',d,methods+i*18)[0]
 if not rva or rva in used: continue
 used.add(rva); b=d[rva]
 if b&3==2: size=b>>2;off=rva+1
 elif b&3==3:
  off=rva+4*(d[rva+1]>>4);size=struct.unpack_from('<I',d,rva+4)[0]
 else: raise ValueError((i,rva,b))
 assert 0<=size<=len(d)-off and off+size<=streams[0][0]
 if size:d[off:off+size]=transform(*prog[7],off,size,16)
print('bodies',len(used),d[0x1b46bc:0x1b46ec].hex())
off,n=streams[2];end=off+n; off+=1;entries=0
while off<end:
 b=d[off]; prefix=1 if b<128 else 2 if b<192 else 4
 size=int.from_bytes(d[off:off+prefix],'big')&{1:127,2:16383,4:0x1fffffff}[prefix]
 off+=prefix
 if size:
  assert off+size<=end,(hex(off),size)
  d[off:off+size]=transform(*prog[4],off,size,16);entries+=1
 off+=size
print('US entries',entries)
# Map container RVAs identically into a single PE section.
d[:0x490]=bytes(0x490)
d[:2]=b'MZ';struct.pack_into('<I',d,60,0x80)
d[128:132]=b'PE\0\0';struct.pack_into('<HHIIIHH',d,132,0x14c,1,0,0,0,224,0x2102)
o=152
def w(pos,fmt,*args):struct.pack_into('<'+fmt,d,pos,*args)
size=len(d); end=(size+511)&~511;d.extend(bytes(end-size))
w(o,'H',0x10b);w(o+4,'I',end-0x400);w(o+20,'III',0x400,0x400,0x400000);w(o+32,'II',512,512);w(o+40,'H',4);w(o+48,'H',4);w(o+56,'II',end,0x400);w(o+68,'H',3);w(o+72,'IIIIII',0x100000,4096,0x100000,4096,0,16);w(o+96+14*8,'II',0x400,72)
s=o+224;d[s:s+8]=b'.text\0\0\0';w(s+8,'IIII',size-0x400,0x400,end-0x400,0x400);w(s+36,'I',0x60000020)
w(0x400,'IHHIII',72,2,5,0x7f93a0,size-0x7f93a0,1)
args.output.parent.mkdir(parents=True, exist_ok=True)
with args.output.open("xb") as output:
    output.write(d)
