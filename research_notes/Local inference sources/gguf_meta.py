# Reads only the key/value header of a GGUF file (no weights) and prints architecture facts.
import struct, sys
def rd(f,fmt):
    return struct.unpack('<'+fmt,f.read(struct.calcsize('<'+fmt)))[0]
def s(f):
    n=rd(f,'Q'); return f.read(n).decode('utf8','replace')
T={0:'B',1:'b',2:'H',3:'h',4:'I',5:'i',6:'f',7:'?',10:'Q',11:'q',12:'d'}
def val(f,t):
    if t==8: return s(f)
    if t==9:
        et=rd(f,'I'); n=rd(f,'Q')
        if et==8:
            for _ in range(n): s(f)
            return f'<{n} strings>'
        sz=struct.calcsize('<'+T[et])
        if n>4096:
            f.seek(sz*n,1); return f'<{n} items>'
        return [rd(f,T[et]) for _ in range(n)]
    return rd(f,T[t])
for p in sys.argv[1:]:
    with open(p,'rb') as f:
        assert f.read(4)==b'GGUF'; rd(f,'I'); nt=rd(f,'Q'); nk=rd(f,'Q')
        print('==',p.split('/')[-1],'tensors',nt)
        for _ in range(nk):
            k=s(f); t=rd(f,'I'); v=val(f,t)
            if not k.startswith('tokenizer') or k.endswith('model'):
                print(' ',k,'=',str(v)[:300])
