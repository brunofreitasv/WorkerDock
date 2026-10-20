## Pré-requisitos

Você precisa de acesso de gerenciamento para alterar as configurações do tenant. Abra [Configurações](#/settings); as abas são **Configurações**, **Provedor de segredos**, **Política de saída**, **Perfis SQL** e **Retention**. O cabeçalho do aplicativo também tem as preferências **Idioma** e **Fuso horário** para sua própria exibição.

## Função

Leitores podem consultar este guia. Salvar configurações do tenant, alterar a política de saída, gerenciar perfis SQL e editar a retenção exige acesso de gerenciamento. Administradores de implantação configuram o provedor de cofre e o local de armazenamento de arquivos.

## Etapas

1. Na aba **Configurações**, atualize **Nome de exibição** ou **Fuso horário padrão de exibição** do tenant e salve. Use as preferências **Idioma** e **Fuso horário** do cabeçalho para sua própria exibição.
2. Em **Provedor de segredos**, confira o provedor configurado e suas opções públicas. Esta página não revela nem aceita valores secretos; configure credenciais pelo provedor de segredos da implantação.
3. Em **Política de saída**, selecione **tenant-jobs** ou **tenant-alerts** e revise hosts permitidos, portas, CIDRs privados e a permissão de HTTP. Salve somente os destinos necessários aos seus fluxos de trabalho. Destinos de loopback, metadados e link-local continuam bloqueados.
4. Em **Perfis SQL**, crie ou atualize um perfil nomeado, selecione catálogos permitidos e opções de autenticação e use o diagnóstico quando necessário. As credenciais do perfil usam referências lógicas de segredo.
5. Em **Retenção e arquivo**, defina por quantos dias manter logs de execução, histórico de execuções e histórico de auditoria. Você pode ativar o arquivamento antes da exclusão e definir a retenção dos arquivos. O local de armazenamento é configurado pelo administrador e não está disponível nesta página.
6. Salve as alterações na área correspondente. Se a página indicar uma versão desatualizada, recarregue e confira os valores atuais antes de editar novamente.

## Resultado

As preferências e políticas operacionais do tenant ficam salvas como configurações versionadas. Alterá-las não transforma o modo de desenvolvimento em memória em comprovação de banco de dados real, provedor de segredos, entrega de saída ou armazenamento de arquivos. Consulte [tarefas do SQL Server](#/help:sql-jobs), [tarefas HTTP](#/help:http-jobs) e [configuração de alertas](#/help:alerts) para fluxos relacionados.
