import { useState, useEffect, type ReactNode, type FormEvent } from 'react';
import {
  BrowserRouter,
  Link,
  Navigate,
  Route,
  Routes,
  useNavigate,
  useParams,
} from 'react-router-dom';
import { QueryClient, QueryClientProvider, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  api,
  externalForm,
  type User,
  type Organization,
  ApiError,
  identity,
} from '@yard/api-client';
import { Button } from '@yard/ui/button';
import { Input } from '@yard/ui/input';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@yard/ui/card';

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } },
});
const messages: Record<string, string> = {
  invalid_credentials: '账号或密码不正确，或账号暂时不可用。',
  registration_closed: '当前未开放注册，请联系管理员。',
  csrf_invalid: '会话已更新，请刷新后重试。',
  password_change_required: '请先修改临时密码。',
  current_password_invalid: '当前密码不正确。',
  verification_invalid_or_expired: '验证码不正确或已过期，请重新获取。',
  verification_resend_too_soon: '请等待六十秒后重新发送。',
  verification_send_limit: '发送次数较多，请稍后重试。',
  email_delivery_failed: '邮件发送失败，请稍后重试。',
  smtp_not_configured: '邮件服务尚未配置，请联系管理员。',
  email_already_used: '此邮箱已被使用。',
  email_exists_sign_in_and_link: '此邮箱已有账号，请登录原账号后绑定 GitHub。',
  last_administrator_protected: '不能停用最后一名管理员。',
  owner_protected: '请先转移组织所有权。',
  permission_denied: '你没有执行此操作的权限。',
  organization_archived: '组织已归档。',
  github_disabled: 'GitHub 登录尚未开启。',
  github_authorization_failed: 'GitHub 授权未完成，请重试。',
  github_already_linked: 'GitHub 身份已绑定。',
  password_required_before_unlink: '请先设置本地密码，再解绑 GitHub。',
  email_verification_required: '请先验证联系邮箱。',
  password_requires_12_to_128_characters: '密码需为 12–128 个字符。',
  invalid_username: '用户名需为 3–32 位，英文字母开头，可含字母、数字、点、下划线或短横线。',
  conflict: '资料与现有记录冲突，请检查后重试。',
  DuplicateUserName: '用户名已被使用。',
  invalid_phone: '请输入有效手机号；国际号码须包含国家码。',
  existing_user_required: '请输入已有账号的精确用户名或已验证邮箱。',
};
function message(e: unknown) {
  return e instanceof ApiError
    ? (messages[e.code] ?? e.code.replaceAll('_', ' '))
    : e instanceof Error
      ? e.message
      : '操作失败，请重试。';
}
function useData<T = any>(path: string, enabled = true) {
  return useQuery<T>({ queryKey: [path], queryFn: () => api<T>(path), enabled });
}
function Field({
  label,
  name,
  type = 'text',
  value,
  required = true,
  help,
}: {
  label: string;
  name: string;
  type?: string;
  value?: string;
  required?: boolean;
  help?: string;
}) {
  return (
    <label className="grid gap-2 text-sm font-medium">
      {label}
      <Input
        name={name}
        aria-label={label}
        type={type}
        defaultValue={value}
        required={required}
        minLength={type === 'password' && name !== 'currentPassword' ? 12 : undefined}
        autoComplete={type === 'password' ? 'current-password' : undefined}
      />
      {help && <span className="text-xs font-normal text-muted-foreground">{help}</span>}
    </label>
  );
}
function Panel({
  title,
  description,
  children,
}: {
  title: string;
  description?: string;
  children: ReactNode;
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
        {description && <CardDescription>{description}</CardDescription>}
      </CardHeader>
      <CardContent className="grid gap-4">{children}</CardContent>
    </Card>
  );
}
function Form({
  children,
  submit = '保存',
  action,
  onSuccess,
  disabled = false,
}: {
  children: ReactNode;
  submit?: string;
  disabled?: boolean;
  action: (f: FormData) => Promise<unknown>;
  onSuccess?: () => void;
}) {
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState(false);
  const q = useQueryClient();
  async function handle(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setPending(true);
    setError('');
    setSuccess(false);
    try {
      await action(f);
      await q.invalidateQueries();
      setSuccess(true);
      onSuccess?.();
    } catch (e) {
      setError(message(e));
    } finally {
      setPending(false);
    }
  }
  return (
    <form onSubmit={handle} className="grid gap-4">
      {children}
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      {success && (
        <p role="status" className="text-sm text-primary">
          操作已完成。
        </p>
      )}
      <Button type="submit" disabled={pending || disabled}>
        {pending ? '处理中…' : submit}
      </Button>
    </form>
  );
}
const value = (f: FormData, k: string) => String(f.get(k) ?? '');
function Failure({ error }: { error: unknown }) {
  return (
    <p role="alert" className="p-4 text-destructive">
      {message(error)}
    </p>
  );
}
function Table({ head, children }: { head: string[]; children: ReactNode }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-left text-sm">
        <thead>
          <tr>
            {head.map((h) => (
              <th key={h} className="border-b p-3 font-medium text-muted-foreground">
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  );
}
function Cell({ children }: { children: ReactNode }) {
  return <td className="border-b p-3 align-top">{children}</td>;
}
function SignedOut() {
  location.assign('/login');
}
function PasswordField({ user }: { user: User }) {
  return user.hasPassword ? (
    <Field label="当前密码" name="currentPassword" type="password" />
  ) : (
    <p className="text-sm text-muted-foreground">请先在上方完成邮箱身份验证。</p>
  );
}
function Login() {
  const n = useNavigate();
  const options = useData('/auth/options');
  const query = new URLSearchParams(location.search);
  return (
    <div className="mx-auto grid max-w-md gap-5">
      <Panel title="欢迎回来" description="使用用户名、已验证邮箱或已核实手机号登录。">
        {query.get('error') && (
          <Failure
            error={new Error(messages[query.get('error')!] ?? 'GitHub 授权未完成，请重试。')}
          />
        )}
        <Form
          submit="登录"
          action={async (f) => {
            const r = await identity.login({
              identifier: value(f, 'identifier'),
              password: value(f, 'password'),
            });
            await queryClient.invalidateQueries();
            n(r.mustChangePassword ? '/account' : '/');
          }}
        >
          <Field label="用户名 / 邮箱 / 手机号" name="identifier" />
          <Field label="密码" name="password" type="password" />
        </Form>
        {options.data?.githubEnabled && (
          <Button variant="outline" onClick={() => externalForm('login')}>
            使用 GitHub 登录
          </Button>
        )}
        <div className="flex justify-between text-sm">
          {options.data?.registrationEnabled ? (
            <Link to="/register">创建账号</Link>
          ) : (
            <span>注册已关闭，请联系管理员。</span>
          )}
          <Link to="/recover">忘记密码</Link>
        </div>
      </Panel>
    </div>
  );
}
function Register() {
  const n = useNavigate();
  return (
    <div className="mx-auto max-w-md">
      <Panel title="创建账号" description="用户名注册后即可使用，邮箱可以稍后验证绑定。">
        <Form
          submit="注册"
          action={(f) =>
            identity.register({
              username: value(f, 'username'),
              password: value(f, 'password'),
            })
          }
          onSuccess={() => n('/login?notice=registered')}
        >
          <Field label="用户名" name="username" help="3–32 位，英文字母开头。" />
          <Field label="密码" name="password" type="password" help="12–128 个字符。" />
        </Form>
        <Link to="/login">已有账号，前往登录</Link>
      </Panel>
    </div>
  );
}
function EmailCode({
  purpose,
  user,
  onVerified,
}: {
  purpose: string;
  user?: User;
  onVerified?: (id: string) => void;
}) {
  const [challenge, setChallenge] = useState('');
  const [sent, setSent] = useState(false);
  const [remaining, setRemaining] = useState(0);
  useEffect(() => {
    if (remaining === 0) return;
    const timer = setTimeout(() => setRemaining(remaining - 1), 1000);
    return () => clearTimeout(timer);
  }, [remaining]);
  return (
    <div className="grid gap-4">
      <Form
        disabled={remaining > 0}
        submit={
          remaining > 0 ? `重新发送（${remaining}s）` : sent ? '重新发送验证码' : '发送验证码'
        }
        action={async (f) => {
          const r = await api('/auth/email/request', 'POST', {
            purpose,
            email: value(f, 'email'),
            password: value(f, 'currentPassword') || null,
          });
          setChallenge(r.challengeId);
          setSent(true);
          setRemaining(r.retryAfter);
        }}
      >
        {purpose !== 'reauth' && (
          <Field label="邮箱地址" name="email" type="email" value={user?.email ?? ''} />
        )}
        {purpose === 'bind-email' && user && <PasswordField user={user} />}
        <p className="text-xs text-muted-foreground">验证码五分钟内有效，每六十秒可重新发送。</p>
      </Form>
      {challenge && (
        <Form
          submit="确认验证码"
          action={async (f) => {
            await api('/auth/email/verify', 'POST', {
              challengeId: challenge,
              code: value(f, 'code'),
            });
            onVerified?.(challenge);
          }}
        >
          <Field label="六位邮箱验证码" name="code" />
          <input type="hidden" name="challengeId" value={challenge} />
        </Form>
      )}
    </div>
  );
}
function Recover() {
  const [proof, setProof] = useState('');
  return (
    <div className="mx-auto max-w-md">
      <Panel title="恢复密码" description="需要账号已验证的联系邮箱；未绑定邮箱时请联系管理员。">
        <EmailCode purpose="recover-password" onVerified={setProof} />
        {proof && (
          <Form
            submit="设置新密码"
            action={(f) =>
              api('/auth/recover-password', 'POST', {
                challengeId: proof,
                password: value(f, 'password'),
              })
            }
            onSuccess={SignedOut}
          >
            <Field label="新密码" name="password" type="password" />
          </Form>
        )}
        <Link to="/login">返回登录</Link>
      </Panel>
    </div>
  );
}
function Account({ user }: { user: User }) {
  return (
    <div className="grid gap-5 lg:grid-cols-2">
      {!user.hasPassword && (
        <Panel title="验证身份" description="无密码账号的敏感操作通过已验证联系邮箱确认。">
          <EmailCode purpose="reauth" user={user} />
        </Panel>
      )}
      <Panel
        title={user.mustChangePassword ? '请修改临时密码' : '修改密码'}
        description="修改后所有设备需要重新登录。"
      >
        <Form
          submit="修改密码"
          action={(f) =>
            api('/auth/change-password', 'POST', {
              currentPassword: value(f, 'currentPassword') || null,
              password: value(f, 'password'),
            })
          }
          onSuccess={SignedOut}
        >
          <PasswordField user={user} />
          <Field label="新密码" name="password" type="password" />
        </Form>
      </Panel>
      {!user.mustChangePassword && (
        <>
          <Panel title="个人资料">
            <Form
              action={(f) =>
                api('/auth/profile', 'PATCH', { displayName: value(f, 'displayName') })
              }
            >
              <Field label="显示名称" name="displayName" value={user.displayName} />
            </Form>
          </Panel>
          <Panel title="用户名" description="实际变更后，所有设备需要重新登录。">
            <Form
              action={(f) =>
                api('/auth/username', 'PATCH', {
                  username: value(f, 'username'),
                  password: value(f, 'currentPassword') || null,
                })
              }
              onSuccess={SignedOut}
            >
              <Field label="用户名" name="username" value={user.username} />
              <PasswordField user={user} />
            </Form>
          </Panel>
          <Panel
            title="邮箱"
            description={user.emailConfirmed ? `当前已验证：${user.email}` : '尚未绑定已验证邮箱。'}
          >
            <EmailCode purpose="bind-email" user={user} onVerified={SignedOut} />
          </Panel>
          <Panel title="手机号">
            <p>{user.phoneNumberConfirmed ? user.phoneNumber : '尚未绑定'}</p>
            <p className="text-sm text-muted-foreground">手机号由管理员核实后维护，不发送短信。</p>
          </Panel>
          <GitHubAccount user={user} />
        </>
      )}
    </div>
  );
}
function GitHubAccount({ user }: { user: User }) {
  const opts = useData('/auth/options');
  if (!opts.data?.githubEnabled) return null;
  const linked = user.externalLogins.includes('github');
  return (
    <Panel
      title="GitHub 登录"
      description={linked ? '已绑定 GitHub' : '绑定后可通过 GitHub 登录原账号。'}
    >
      <Form
        submit={linked ? '解绑 GitHub' : '绑定 GitHub'}
        action={async (f) => {
          const password = value(f, 'currentPassword');
          if (linked) {
            await api('/auth/external/github', 'DELETE', { password });
            SignedOut();
          } else await externalForm('link', password);
        }}
      >
        <PasswordField user={user} />
      </Form>
    </Panel>
  );
}
function GitHubRegister() {
  const pending = useData('/auth/external/registration');
  const [proof, setProof] = useState('');
  if (pending.isPending) return <p>正在读取 GitHub 授权…</p>;
  if (pending.error) return <Failure error={pending.error} />;
  return (
    <div className="mx-auto grid max-w-md gap-5">
      <Panel title="完成 GitHub 注册" description="选择用户名并确认联系邮箱。本地密码可选。">
        <Form
          submit="完成注册"
          action={async (f) => {
            await api('/auth/external/registration', 'POST', {
              username: value(f, 'username'),
              email: value(f, 'email'),
              password: value(f, 'password') || null,
              challengeId: proof || null,
            });
            location.assign('/');
          }}
        >
          <Field label="用户名" name="username" />
          <Field label="联系邮箱" name="email" type="email" value={pending.data.email ?? ''} />
          <Field label="本地密码（可选）" name="password" type="password" required={false} />
          <p className="text-sm text-muted-foreground">
            {pending.data.emailVerified
              ? 'GitHub 已验证此邮箱；更换邮箱时需在下方验证。'
              : '请先在下方验证联系邮箱。'}
          </p>
        </Form>
      </Panel>
      <Panel title="验证其他联系邮箱">
        <EmailCode purpose="github-registration" onVerified={setProof} />
      </Panel>
    </div>
  );
}
function Home() {
  const orgs = useData<Organization[]>('/organizations/');
  const invites = useData<any[]>('/invitations');
  return (
    <div className="grid gap-5">
      <Panel title="我的组织" description="组织是成员、权限和业务数据的独立边界。">
        {orgs.error ? (
          <Failure error={orgs.error} />
        ) : orgs.data?.length ? (
          <div className="grid gap-3 md:grid-cols-2">
            {orgs.data.map((o) => (
              <Link
                className="rounded-lg border p-4 hover:bg-muted"
                to={'/organizations/' + o.id}
                key={o.id}
              >
                <strong>{o.name}</strong>
                <p className="mt-2 text-sm text-muted-foreground">
                  {o.archived ? '已归档' : '进入组织'}
                </p>
              </Link>
            ))}
          </div>
        ) : (
          <p className="text-muted-foreground">还没有组织。创建一个，或接受下方邀请。</p>
        )}
        <Form
          submit="创建组织"
          action={(f) => api('/organizations/', 'POST', { name: value(f, 'name') })}
        >
          <Field label="组织名称" name="name" />
        </Form>
      </Panel>
      <Panel title="待处理邀请">
        {invites.data?.length ? (
          invites.data.map((x) => (
            <div key={x.id} className="flex items-center justify-between gap-4 border-b pb-3">
              <span>{x.organizationName}</span>
              <Form
                submit="接受邀请"
                action={() => api('/invitations/' + x.id + '/accept', 'POST')}
              >
                {null}
              </Form>
            </div>
          ))
        ) : (
          <p className="text-muted-foreground">暂无邀请。</p>
        )}
      </Panel>
    </div>
  );
}
function OrganizationPage({ user }: { user: User }) {
  const { id } = useParams();
  const path = '/organizations/' + id;
  const org = useData<any>(path);
  const members = useData<any[]>(path + '/members');
  const roles = useData<any[]>(path + '/roles');
  const perms = useData<string[]>(path + '/permissions');
  const invitations = useData<any[]>(path + '/invitations');
  const audit = useData<any[]>(path + '/audit');
  const [tab, setTab] = useState('members');
  if (org.error) return <Failure error={org.error} />;
  if (!org.data) return <p>正在读取组织…</p>;
  const o = org.data;
  return (
    <div className="grid gap-5">
      <div>
        <Link to="/">← 我的组织</Link>
        <h1 className="mt-3 text-2xl font-semibold">
          {o.name}
          {o.archived && '（已归档）'}
        </h1>
      </div>
      <nav className="flex flex-wrap gap-2">
        {[
          ['members', '成员'],
          ['roles', '角色与权限'],
          ['invitations', '邀请'],
          ['settings', '组织设置'],
          ['audit', '操作记录'],
        ].map(([key, label]) => (
          <Button
            key={key}
            variant={tab === key ? 'default' : 'outline'}
            onClick={() => setTab(key)}
          >
            {label}
          </Button>
        ))}
      </nav>
      {tab === 'members' && (
        <Panel title="组织成员">
          {members.error ? (
            <Failure error={members.error} />
          ) : (
            <Table head={['成员', '角色', '操作']}>
              {members.data?.map((m) => (
                <tr key={m.id}>
                  <Cell>
                    {m.displayName}
                    <div className="text-muted-foreground">
                      {m.username}
                      {m.id === o.ownerId ? ' · 所有者' : ''}
                    </div>
                  </Cell>
                  <Cell>
                    {m.id === o.ownerId ? (
                      '所有权限'
                    ) : (
                      <Form
                        submit="保存角色"
                        action={(f) =>
                          api(path + '/members/' + m.id + '/roles', 'PUT', {
                            roleIds: f.getAll('roles'),
                          })
                        }
                      >
                        {roles.data?.map((r) => (
                          <label key={r.id} className="flex gap-2">
                            <input
                              type="checkbox"
                              name="roles"
                              value={r.id}
                              defaultChecked={m.roleIds.includes(r.id)}
                            />
                            {r.name}
                          </label>
                        ))}
                      </Form>
                    )}
                  </Cell>
                  <Cell>
                    {m.id !== o.ownerId && (
                      <Form
                        submit="移除成员"
                        action={() => api(path + '/members/' + m.id, 'DELETE')}
                      >
                        {null}
                      </Form>
                    )}
                  </Cell>
                </tr>
              ))}
            </Table>
          )}
        </Panel>
      )}
      {tab === 'roles' && (
        <>
          <Panel title="角色与权限">
            {roles.error ? (
              <Failure error={roles.error} />
            ) : (
              roles.data?.map((role) => (
                <RoleEditor key={role.id} path={path} role={role} permissions={perms.data ?? []} />
              ))
            )}
          </Panel>
          <Panel title="新增角色">
            <RoleEditor path={path} permissions={perms.data ?? []} />
          </Panel>
        </>
      )}
      {tab === 'invitations' && (
        <>
          <Panel title="邀请已有账号">
            <Form
              submit="发送站内邀请"
              action={(f) =>
                api(path + '/invitations', 'POST', { identifier: value(f, 'identifier') })
              }
            >
              <Field label="精确用户名 / 已验证邮箱" name="identifier" />
            </Form>
          </Panel>
          <Panel title="邀请记录">
            {invitations.error ? (
              <Failure error={invitations.error} />
            ) : (
              invitations.data?.map((x) => (
                <div key={x.id} className="grid gap-2 border-b py-3">
                  <span>
                    {x.userId} · {x.status} · {new Date(x.expiresAt).toLocaleDateString()}
                  </span>
                  {x.status === 'pending' && (
                    <Form
                      submit="撤销邀请"
                      action={() => api(path + '/invitations/' + x.id, 'DELETE')}
                    >
                      {null}
                    </Form>
                  )}
                </div>
              ))
            )}
          </Panel>
        </>
      )}
      {tab === 'settings' && (
        <>
          <Panel title="组织资料">
            <Form action={(f) => api(path, 'PATCH', { name: value(f, 'name') })}>
              <Field label="组织名称" name="name" value={o.name} />
            </Form>
          </Panel>
          {o.ownerId === user.id && (
            <>
              <Panel title={o.archived ? '恢复组织' : '归档组织'}>
                <Form
                  submit={o.archived ? '恢复组织' : '归档组织'}
                  action={() => api(path + '/archive', 'PATCH', { archived: !o.archived })}
                >
                  {null}
                </Form>
              </Panel>
              <Panel title="转移所有权" description="新所有者必须是本组织的有效成员。">
                <Form
                  submit="转移所有权"
                  action={(f) =>
                    api(path + '/transfer', 'POST', {
                      userId: value(f, 'userId'),
                      password: value(f, 'currentPassword') || null,
                    })
                  }
                >
                  <label className="grid gap-2">
                    新所有者
                    <select className="rounded border p-2" name="userId" required>
                      {members.data
                        ?.filter((m) => m.id !== user.id && m.isEnabled)
                        .map((m) => (
                          <option key={m.id} value={m.id}>
                            {m.username}
                          </option>
                        ))}
                    </select>
                  </label>
                  <PasswordField user={user} />
                </Form>
              </Panel>
            </>
          )}
        </>
      )}
      {tab === 'audit' && (
        <Panel title="组织操作记录">
          {audit.error ? (
            <Failure error={audit.error} />
          ) : (
            <AuditTable entries={audit.data ?? []} />
          )}
        </Panel>
      )}
    </div>
  );
}
const permissionNames: Record<string, string> = {
  'organization.read': '查看组织',
  'organization.update': '维护组织资料',
  'members.read': '查看成员',
  'members.manage': '管理成员',
  'roles.read': '查看角色',
  'roles.manage': '管理角色与授权',
  'invitations.manage': '管理邀请',
  'audit.read': '查看操作记录',
};
function RoleEditor({
  path,
  role,
  permissions,
}: {
  path: string;
  role?: any;
  permissions: string[];
}) {
  return (
    <div className="grid gap-3 border-b pb-5">
      <Form
        submit={role ? '保存角色' : '创建角色'}
        action={(f) =>
          api(path + '/roles' + (role ? '/' + role.id : ''), role ? 'PUT' : 'POST', {
            name: value(f, 'name'),
            permissions: f.getAll('permissions'),
          })
        }
      >
        <Field label="角色名称" name="name" value={role?.name} />
        <div className="grid gap-2 sm:grid-cols-2">
          {permissions.map((p) => (
            <label className="flex items-center gap-2 text-sm" key={p}>
              <input
                type="checkbox"
                name="permissions"
                value={p}
                defaultChecked={role?.permissions.includes(p)}
              />
              {permissionNames[p] ?? p}
            </label>
          ))}
        </div>
      </Form>
      {role && (
        <Form submit="删除角色" action={() => api(path + '/roles/' + role.id, 'DELETE')}>
          {null}
        </Form>
      )}
    </div>
  );
}
function AuditTable({ entries }: { entries: any[] }) {
  return (
    <Table head={['操作', '操作者', '时间']}>
      {entries.map((x) => (
        <tr key={x.id}>
          <Cell>
            {x.action}
            <p className="break-all text-xs text-muted-foreground">{x.subject}</p>
          </Cell>
          <Cell>{x.actorId ?? '命令行'}</Cell>
          <Cell>{new Date(x.at).toLocaleString()}</Cell>
        </tr>
      ))}
    </Table>
  );
}
function Admin({ user }: { user: User }) {
  const [tab, setTab] = useState('users');
  const [query, setQuery] = useState('');
  const [page, setPage] = useState(1);
  const users = useData<any>('/admin/users?page=' + page + '&q=' + encodeURIComponent(query));
  const settings = useData('/admin/registration');
  const orgs = useData<any[]>('/admin/organizations');
  const audit = useData<any[]>('/admin/audit');
  const mail = useData<any[]>('/__yard/mail', tab === 'mail');
  const [selected, setSelected] = useState<any>(null);
  if (!user.isPlatformAdministrator) return <Failure error={new Error('需要平台管理员权限。')} />;
  return (
    <div className="grid gap-5">
      <h1 className="text-2xl font-semibold">平台管理</h1>
      <nav className="flex flex-wrap gap-2">
        {[
          ['users', '账号'],
          ['organizations', '组织'],
          ['settings', '注册设置'],
          ['audit', '操作记录'],
          ['mail', '开发收件箱'],
        ].map(([k, v]) => (
          <Button key={k} variant={tab === k ? 'default' : 'outline'} onClick={() => setTab(k)}>
            {v}
          </Button>
        ))}
      </nav>
      {tab === 'users' && (
        <>
          <Panel title="账号列表">
            <Input
              aria-label="搜索账号"
              placeholder="搜索用户名或邮箱"
              value={query}
              onChange={(e) => {
                setQuery(e.target.value);
                setPage(1);
              }}
            />
            {users.error ? (
              <Failure error={users.error} />
            ) : (
              <Table head={['账号', '状态', '登录标识', '操作']}>
                {users.data?.items.map((u: any) => (
                  <tr key={u.id}>
                    <Cell>
                      {u.username}
                      {u.isPlatformAdministrator && ' · 平台管理员'}
                    </Cell>
                    <Cell>
                      {u.isEnabled ? '启用' : '停用'}
                      {u.mustChangePassword && ' · 待改密'}
                    </Cell>
                    <Cell>
                      {u.emailConfirmed ? u.email : '未验证邮箱'}
                      <br />
                      {u.phoneNumberConfirmed ? u.phoneNumber : '未核实手机号'}
                    </Cell>
                    <Cell>
                      <Button variant="outline" onClick={() => setSelected(u)}>
                        管理
                      </Button>
                    </Cell>
                  </tr>
                ))}
              </Table>
            )}
            <div className="flex items-center justify-between">
              <Button variant="outline" disabled={page === 1} onClick={() => setPage(page - 1)}>
                上一页
              </Button>
              <span>
                第 {page} 页 · {users.data?.total ?? 0} 个账号
              </span>
              <Button
                variant="outline"
                disabled={page * 20 >= (users.data?.total ?? 0)}
                onClick={() => setPage(page + 1)}
              >
                下一页
              </Button>
            </div>
          </Panel>
          {selected && (
            <Panel title={'管理 ' + selected.username}>
              <Button variant="ghost" onClick={() => setSelected(null)}>
                关闭
              </Button>
              <div className="grid gap-5 md:grid-cols-2">
                <Form
                  submit={selected.isEnabled ? '停用账号' : '启用账号'}
                  action={() =>
                    api('/admin/users/' + selected.id + '/state', 'PATCH', {
                      enabled: !selected.isEnabled,
                    })
                  }
                  onSuccess={() => setSelected(null)}
                >
                  {null}
                </Form>
                <Form
                  submit={selected.isPlatformAdministrator ? '撤销平台管理员' : '设为平台管理员'}
                  action={(f) =>
                    api('/admin/users/' + selected.id + '/administrator', 'PATCH', {
                      administrator: !selected.isPlatformAdministrator,
                      password: value(f, 'currentPassword') || null,
                    })
                  }
                  onSuccess={() => setSelected(null)}
                >
                  <PasswordField user={user} />
                </Form>
                <Form
                  submit="撤销所有会话"
                  action={() => api('/admin/users/' + selected.id + '/revoke-sessions', 'POST')}
                >
                  {null}
                </Form>
                <Form
                  submit="重置临时密码"
                  action={(f) =>
                    api('/admin/users/' + selected.id + '/reset-password', 'POST', {
                      currentPassword: value(f, 'currentPassword'),
                      password: value(f, 'password'),
                    })
                  }
                >
                  <PasswordField user={user} />
                  <Field label="新的临时密码" name="password" type="password" />
                </Form>
                <Form
                  submit="保存已核实手机号"
                  action={(f) =>
                    api('/admin/users/' + selected.id + '/phone', 'PATCH', {
                      phoneNumber: value(f, 'phoneNumber') || null,
                      ownershipConfirmed: f.get('confirmed') === 'on',
                      password: value(f, 'currentPassword') || null,
                    })
                  }
                >
                  <Field
                    label="手机号（留空清除）"
                    name="phoneNumber"
                    value={selected.phoneNumber ?? ''}
                    required={false}
                  />
                  <label className="flex gap-2 text-sm">
                    <input type="checkbox" name="confirmed" />
                    我已核实号码归属
                  </label>
                  <PasswordField user={user} />
                </Form>
              </div>
            </Panel>
          )}
          <Panel title="创建账号" description="交付临时密码后，用户首次登录必须修改。">
            <Form
              submit="创建账号"
              action={(f) =>
                api('/admin/users', 'POST', {
                  username: value(f, 'username'),
                  password: value(f, 'password'),
                })
              }
            >
              <Field label="用户名" name="username" />
              <Field label="临时密码" name="password" type="password" />
            </Form>
          </Panel>
        </>
      )}
      {tab === 'settings' && (
        <Panel title="公开注册">
          <Form
            action={(f) =>
              api('/admin/registration', 'PATCH', { enabled: f.get('enabled') === 'on' })
            }
          >
            <label className="flex gap-2">
              <input
                type="checkbox"
                name="enabled"
                key={String(settings.data?.enabled)}
                defaultChecked={settings.data?.enabled}
              />
              允许新用户注册
            </label>
          </Form>
        </Panel>
      )}
      {tab === 'organizations' && (
        <Panel title="全部组织">
          <Table head={['组织', '状态', '所有者', '操作']}>
            {orgs.data?.map((o) => (
              <tr key={o.id}>
                <Cell>{o.name}</Cell>
                <Cell>{o.archived ? '归档' : '有效'}</Cell>
                <Cell>{o.ownerId}</Cell>
                <Cell>
                  <Form
                    submit={o.archived ? '恢复组织' : '归档组织'}
                    action={() =>
                      api('/admin/organizations/' + o.id + '/archive', 'PATCH', {
                        archived: !o.archived,
                      })
                    }
                  >
                    {null}
                  </Form>
                </Cell>
              </tr>
            ))}
          </Table>
        </Panel>
      )}
      {tab === 'audit' && (
        <Panel title="平台操作记录">
          <AuditTable entries={audit.data ?? []} />
        </Panel>
      )}
      {tab === 'mail' && (
        <Panel
          title="开发收件箱"
          description="仅 Development 可用，展示此 checkout 实际生成的邮件。"
        >
          {mail.error ? (
            <Failure error={mail.error} />
          ) : (
            mail.data?.map((m) => (
              <article className="grid gap-2 border-b pb-4" key={m.id}>
                <strong>{m.subject}</strong>
                <span className="text-sm text-muted-foreground">
                  {m.recipient} · {new Date(m.at).toLocaleString()}
                </span>
                <pre className="whitespace-pre-wrap break-words rounded bg-muted p-4 text-sm">
                  {m.text}
                </pre>
              </article>
            ))
          )}
        </Panel>
      )}
    </div>
  );
}
function Application({ name, base }: { name: string; base: string }) {
  const user = useQuery<User | null>({
    queryKey: ['/auth/me'],
    queryFn: async () => {
      try {
        return await api<User>('/auth/me');
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) return null;
        throw error;
      }
    },
  });
  const n = useNavigate();
  const rootAdmin = base === '/admin/';
  const signedIn = user.data;
  return (
    <>
      <header className="border-b bg-card">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-4 px-5 py-4">
          <Link className="text-lg font-semibold tracking-tight" to="/">
            {name}
          </Link>
          <nav className="flex flex-wrap items-center gap-4 text-sm">
            {signedIn ? (
              <>
                <a href="/">组织</a>
                <a href="/account">账号设置</a>
                {signedIn.isPlatformAdministrator && <a href="/admin/">平台管理</a>}
                <button
                  onClick={async () => {
                    await api('/auth/logout', 'POST');
                    queryClient.clear();
                    n('/login');
                  }}
                >
                  退出
                </button>
              </>
            ) : (
              <Link to="/login">登录</Link>
            )}
          </nav>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-4 py-8 sm:px-6">
        {user.isPending ? (
          <p>正在加载…</p>
        ) : (
          <Routes>
            <Route path="/login" element={<Login />} />
            <Route path="/register" element={<Register />} />
            <Route path="/recover" element={<Recover />} />
            <Route path="/github-register" element={<GitHubRegister />} />
            <Route
              path="/"
              element={
                signedIn ? (
                  rootAdmin ? (
                    <Admin user={signedIn} />
                  ) : signedIn.mustChangePassword ? (
                    <Navigate to="/account" />
                  ) : (
                    <Home />
                  )
                ) : (
                  <Navigate to="/login" />
                )
              }
            />
            <Route
              path="/account"
              element={signedIn ? <Account user={signedIn} /> : <Navigate to="/login" />}
            />
            <Route
              path="/admin/*"
              element={signedIn ? <Admin user={signedIn} /> : <Navigate to="/login" />}
            />
            <Route
              path="/organizations/:id"
              element={signedIn ? <OrganizationPage user={signedIn} /> : <Navigate to="/login" />}
            />
            <Route
              path="*"
              element={
                <Panel title="页面不存在">
                  <Link to="/">返回首页</Link>
                </Panel>
              }
            />
          </Routes>
        )}
      </main>
      <footer className="mx-auto max-w-6xl border-t px-5 py-5 text-xs text-muted-foreground">
        {name} · Workspace
      </footer>
    </>
  );
}
export function Platform({ name, base = '/' }: { name: string; base?: string }) {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter basename={base.replace(/\/$/, '') || '/'}>
        <Application name={name} base={base} />
      </BrowserRouter>
    </QueryClientProvider>
  );
}
