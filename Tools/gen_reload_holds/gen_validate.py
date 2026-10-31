import sys,json,math
sys.path.insert(0,'.')
import numpy as np
exec(open('gen_holds.py',encoding='utf-8').read().split("if __name__=='__main__':")[0])
gen=json.load(open(sys.argv[1]))['result']
rec=json.load(open('ReloadGrips.before-repair.json',encoding='utf-8'))
def eq(e): return qmul(qmul(axisq(e[1],[0,1,0]),axisq(e[0],[1,0,0])),axisq(e[2],[0,0,1]))
off=axisq(90,[1,0,0])
print('--- generated vs your M4 recordings (position cm, rotation deg with the model offset, four fingers mean/max deg)')
for t in ('2','3','4','5','6','7'):
    k='m4a1|'+t
    for half in ('pull','insert'):
        g=gen['grips'].get(k,{}).get(half); r=rec[k][half]
        if not g:
            print(k,half,'NOT GENERATED'); continue
        dp=np.linalg.norm(np.array(g['position'])-np.array(r['position']))*100
        dr=ang(qmul(eq(g['rotation']),off),eq(r['rotation']))
        F=[ang(eq(g['fingers'][i*3:i*3+3]),eq(r['fingers'][i*3:i*3+3])) for i in range(4,16)]
        print(f'{k} {half:6s} {dp:5.1f} cm {dr:5.1f} deg fingers {np.mean(F):5.1f}/{max(F):5.1f} frames {g["frames"]} palmOff {g["palmOff"]}')
g=gen['grips'].get('m4a1|0',{})
if g.get('pull'): print('m4a1|0 pull vs built-in',round(float(np.linalg.norm(np.array(g['pull']['position'])-np.array([-0.186,-0.033,0.115]))*100),1),'cm',round(ang(qmul(eq(g['pull']['rotation']),off),eq((352.6,250,271.1))),1),'deg')
if g.get('insert'): print('m4a1|0 insert vs built-in',round(float(np.linalg.norm(np.array(g['insert']['position'])-np.array([-0.198,-0.198,0.022]))*100),1),'cm',round(ang(qmul(eq(g['insert']['rotation']),off),eq((337.8,265.3,180.4))),1),'deg')
h=gen['handles'].get('m4a1')
if h: print('handle vs baked',round(float(np.linalg.norm(np.array(h['position'])-np.array([-0.1438,0.0636,-0.0651]))*100),1),'cm',round(ang(eq(h['rotation']),eq((324.8,110.6,32.1))),1),'deg travel',h['travel'])
p=gen['palms'].get('m4a1')
if p: print('palm new vs baked',p['newMagazine']['position'],round(ang(qmul(eq(p['newMagazine']['rotation']),off),eq((345,300.1,180))),1),'deg; old',p['oldMagazine']['position'],round(ang(qmul(eq(p['oldMagazine']['rotation']),off),eq((277.6,120.5,359.8))),1),'deg')
print('report',json.load(open(sys.argv[1]))['report'][:6])
