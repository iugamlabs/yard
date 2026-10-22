// Local protocol fixture. No external service, credentials or email delivery.
import http from 'node:http';
import {randomBytes,createHash} from 'node:crypto';
const port=Number(process.argv[2]);let account={id:12345,email:'external@example.test',verified:true};const codes=new Map();let exchanges=0;
const json=(res,status,value)=>{res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(value));};
const server=http.createServer(async(req,res)=>{try{const url=new URL(req.url,'http://127.0.0.1:'+port);
if(url.pathname==='/__test/account'&&req.method==='POST'){let input='';for await(const chunk of req)input+=chunk;account={...account,...JSON.parse(input)};json(res,200,{ready:true});return;}
if(url.pathname==='/__test/status'){json(res,200,{exchanges});return;}
if(url.pathname==='/login/oauth/authorize'){if(url.searchParams.get('client_id')!=='stub-client'||url.searchParams.get('code_challenge_method')!=='S256')throw new Error('Invalid authorization/PKCE');const code=randomBytes(20).toString('hex');codes.set(code,url.searchParams.get('code_challenge'));const callback=new URL(url.searchParams.get('redirect_uri'));callback.searchParams.set('code',code);callback.searchParams.set('state',url.searchParams.get('state'));res.writeHead(302,{location:callback.href});res.end();return;}
if(url.pathname==='/login/oauth/access_token'){let body='';for await(const chunk of req)body+=chunk;const f=new URLSearchParams(body);const expected=codes.get(f.get('code'));codes.delete(f.get('code'));if(!expected||expected!==createHash('sha256').update(f.get('code_verifier')??'').digest('base64url')||f.get('client_secret')!=='stub-secret')throw new Error('Invalid single-use code or verifier');exchanges++;json(res,200,{access_token:'stub-access',token_type:'Bearer',scope:'read:user user:email'});return;}
if(url.pathname==='/user'){json(res,200,{id:account.id,login:'external-user',name:'External user',email:account.email});return;}
if(url.pathname==='/user/emails'){json(res,200,[{email:account.email,primary:true,verified:account.verified}]);return;}
json(res,404,{error:'not_found'});
}catch(error){json(res,400,{error:error.message});}});server.listen(port,'127.0.0.1',()=>process.stdout.write('GitHub protocol fixture ready\n'));for(const signal of ['SIGTERM','SIGINT'])process.on(signal,()=>server.close(()=>process.exit(0)));
