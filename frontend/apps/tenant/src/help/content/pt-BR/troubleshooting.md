## Pré-requisitos

Acesso Read basta para consultar painéis do tenant, monitoramento, workers e histórico de auditoria. Alterações em configurações e tarefas exigem acesso Manage. Para serviços do host, configuração do banco ou credenciais de implantação, anote o horário e o resumo seguro do erro e contate o administrador do WorkerDock.

## Permissão

Use a interface do tenant para consultar estados atuais e códigos de resultado seguros. Não copie senhas, strings de conexão, valores secretos, SQL bruto ou corpos sensíveis de solicitações para mensagens de suporte. Operadores de implantação usam os guias de infraestrutura do código-fonte para alterar o ambiente.

## Etapas

1. Abra o [Painel](#/dashboard) e anote o horário da última atualização. Um aviso de falha ao atualizar significa que os valores exibidos podem estar desatualizados; não significa que os dados estejam vazios.
2. Abra [Monitoramento](#/monitoring), localize a tarefa e confira se está ativa, desativada ou arquivada, as falhas recentes e o estado da agenda.
3. Abra a execução afetada e consulte o estado, as tentativas, o código de resultado seguro, a certeza do resultado e a linha do tempo. Anote o ID da execução e o ID de correlação, se estiverem disponíveis.
4. Consulte [Workers](#/workers) para verificar disponibilidade e atualização do heartbeat. Compare a capacidade do worker com o ID e a versão exatos do modelo usado pela tarefa.
5. Para problemas de configuração ou integração, abra [Configurações](#/settings), se tiver acesso Manage, e confira o perfil SQL, a referência de segredo ou a política de saída pertinente. Não altere uma política de produção apenas para fazer o erro desaparecer.
6. Se o problema continuar, informe ao administrador os identificadores seguros, o horário com fuso, o estado relevante e as etapas já tentadas.

## Exemplo prático

Se uma execução continuar na fila, confira sua linha do tempo e seu estado, o heartbeat do runtime, o limite de processos filhos, os limites do job/modelo e a versão exata do modelo. Se a execução mostrar um código seguro de conexão SQL, um usuário Manage pode revisar o perfil selecionado e um operador pode validar as permissões do destino e a configuração do provedor de segredos. Evite iniciar a tarefa repetidamente até entender o efeito externo e o estado atual da execução.

## Verificação

Depois da correção responsável, atualize a página afetada e confira o horário **Atualizado**. Confirme se o heartbeat ou a configuração estão atualizados e consulte o resultado e a linha do tempo de uma nova execução ou de uma repetição segura. A atualização bem-sucedida da interface comprova apenas que a solicitação da API exibida funcionou; ela não comprova um efeito externo sem confirmação no sistema de destino.

## Recuperação

| Sintoma | Primeiras verificações | Próxima ação segura |
| --- | --- | --- |
| A página mostra dados desatualizados ou indisponíveis | Horário Atualizado, aviso de atualização, saúde do armazenamento configurado | Atualize uma vez. Se persistir, peça a um operador para verificar o SQL e os serviços; indisponível não significa vazio. |
| A gravação das configurações é negada | Função Manage e declaração da função no provedor de identidade | Peça a um administrador para corrigir o acesso. Não contorne a interface com uma solicitação direta à API. |
| Configurações informam que a versão está desatualizada | Se outro gerente alterou a configuração | Recarregue, confira o valor atual e aplique apenas a edição necessária. |
| Tarefa permanece na fila ou falha antes de iniciar | Estado do runtime, disponibilidade do SQL, limites e ID/versão do modelo | Peça ao operador para conferir o serviço único de runtime, o acesso ao SQL e o catálogo instalado. |
| Tarefa SQL ou HTTP não alcança o destino | Perfil selecionado ou política de destino, referência de segredo, host/porta, TLS e disponibilidade do destino | Consulte [tarefas SQL](#/help:sql-jobs) ou [tarefas HTTP](#/help:http-jobs) e peça ao operador para verificar o acesso da implantação. |
| A entrega de alerta falha | Regra, destino, estado da entrega, política de saída e saúde do runtime | Consulte [regras de alerta](#/help:alert-rules) e [entregas de alertas](#/help:incidents-deliveries); não cole credenciais de destino na interface. |
| A manutenção de arquivos falha | Diretório de arquivos, permissões do runtime, capacidade do disco e estado da integridade | Preserve os registros de origem e siga o [guia de retenção](#/help:retention) com um operador. |

## Guias relacionados

- [Monitore execuções](#/help:monitoring)
- [Consulte logs e eventos](#/help:execution-events)
- [Verifique a saúde dos workers](#/help:workers)
- [Gerencie configurações do tenant](#/help:settings)
- Referência para operadores: `docs/how-to/troubleshooting.md`
